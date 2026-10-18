using MelodyBridge.Application;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// ExecuteAsync of the three scheduling services with a shortened poll
/// interval. The audit found these loops untested (their bodies were
/// re-implemented in tests) and the sync job schedule had no consumer at
/// all, so this fixture drives the real services against real SQLite,
/// real stores and real files.
/// </summary>
[TestFixture]
public class BackgroundServiceExecuteTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-bg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "services.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FastSyncJobScheduler(IServiceProvider sp)
        : SyncJobSchedulingBackgroundService(sp, NullLogger<SyncJobSchedulingBackgroundService>.Instance)
    {
        internal override TimeSpan PollInterval => TimeSpan.FromMilliseconds(50);
    }

    private sealed class FastAutoSync(IServiceProvider sp, ILogger<AutoSyncBackgroundService> logger)
        : AutoSyncBackgroundService(sp, logger)
    {
        internal override TimeSpan PollInterval => TimeSpan.FromMilliseconds(50);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(exception is null
                ? formatter(state, exception)
                : formatter(state, exception) + " :: " + exception);
    }

    private sealed class FastScanScheduler(IServiceProvider sp)
        : ScanSchedulingBackgroundService(sp, NullLogger<ScanSchedulingBackgroundService>.Instance)
    {
        internal override TimeSpan PollInterval => TimeSpan.FromMilliseconds(50);
    }

    /// <summary>Runs the service until the condition holds, then stops it.</summary>
    private static async Task RunUntilAsync(BackgroundService service, Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return;
                await Task.Delay(50);
            }
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ServiceProvider Provider(params (Type Service, object Instance)[] bindings)
    {
        var services = new ServiceCollection();
        foreach (var (service, instance) in bindings)
            services.AddSingleton(service, instance);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task SyncJobScheduler_RunsDueJobs_AndNeverManualOnes()
    {
        var dir = Path.Combine(_root, "sched-music");
        Directory.CreateDirectory(dir);
        var trackPath = Path.Combine(dir, "song.mp3");
        await File.WriteAllBytesAsync(trackPath, TestAudio.MinimalMp3());
        var m3u = Path.Combine(_root, "scheduled.m3u");
        var manualM3u = Path.Combine(_root, "manual.m3u");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-sched",
                Name = "Scheduled",
                SourceUrl = "stub:sched",
                TargetDirectory = dir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-sched", Title = "Song", Artist = "A",
                        DownloadStatus = "downloaded", CurrentPath = trackPath, Position = 0,
                    },
                },
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-hourly", Name = "Hourly", SourceId = "pl-sched",
                OutputTarget = "M3uFile", M3uOutputPath = m3u, Schedule = "Hourly",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-manual", Name = "Manual", SourceId = "pl-sched",
                OutputTarget = "M3uFile", M3uOutputPath = manualM3u, Schedule = "Manual",
            });
            await db.SaveChangesAsync();
        }

        var runner = new SyncJobRunner(_factory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(), NullLogger<SyncJobRunner>.Instance);
        var sp = Provider(
            (typeof(IDbContextFactory<MelodyBridgeDbContext>), _factory),
            (typeof(ISyncJobRunner), runner));

        // The runner mirrors the Sync jobs page: the job row is stamped in
        // a second context after the output is written, so wait for the
        // stamp before cancelling (a cancel can abort the history write).
        await RunUntilAsync(new FastSyncJobScheduler(sp), async () =>
        {
            await using var db = _factory.CreateDbContext();
            var job = await db.SyncJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == "job-hourly");
            return job?.LastRunAt is not null;
        }, TimeSpan.FromSeconds(15));

        Assert.That(File.Exists(m3u), Is.True, "the due job must run while the app is up");
        Assert.That(File.Exists(manualM3u), Is.False, "a manual job never runs on its own");

        await using var verify = _factory.CreateDbContext();
        var hourly = await verify.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "job-hourly");
        Assert.That(hourly.LastRunStatus, Is.EqualTo("Completed"));
        Assert.That(hourly.LastRunAt, Is.Not.Null);
        var manual = await verify.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "job-manual");
        Assert.That(manual.LastRunAt, Is.Null);
    }

    [Test]
    public async Task SyncJobScheduler_NewScheduledJob_WaitsForItsNextFire()
    {
        // A job saved right now with a daily schedule must not run on the
        // first poll: it waits for its next scheduled time. The creation
        // stamp is the anchor, just like the Sync jobs page writes it.
        var m3u = Path.Combine(_root, "too-early.m3u");
        await using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-fresh", Name = "Fresh", SourceId = "missing",
                OutputTarget = "M3uFile", M3uOutputPath = m3u, Schedule = "Daily",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var runner = new SyncJobRunner(_factory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(), NullLogger<SyncJobRunner>.Instance);
        var sp = Provider(
            (typeof(IDbContextFactory<MelodyBridgeDbContext>), _factory),
            (typeof(ISyncJobRunner), runner));

        using (var cts = new CancellationTokenSource())
        {
            var service = new FastSyncJobScheduler(sp);
            await service.StartAsync(cts.Token);
            // Several poll cycles: the old rule ran it within one.
            await Task.Delay(500);
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }

        await using var verify = _factory.CreateDbContext();
        var job = await verify.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "job-fresh");
        Assert.Multiple(() =>
        {
            Assert.That(job.LastRunAt, Is.Null, "a new daily job must wait for its next fire");
            Assert.That(File.Exists(m3u), Is.False);
        });
    }

    [Test]
    public async Task SyncJobScheduler_NeverRunJob_CreatedWhileTheAppWasOff_CatchesUp()
    {
        // Created two days ago, never run: the next fire after creation is
        // in the past, so the first poll runs it (a missed window is not
        // lost just because the app was off).
        var dir = Path.Combine(_root, "catchup-music");
        Directory.CreateDirectory(dir);
        var trackPath = Path.Combine(dir, "song.mp3");
        await File.WriteAllBytesAsync(trackPath, TestAudio.MinimalMp3());
        var m3u = Path.Combine(_root, "catchup.m3u");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-catchup",
                Name = "Catchup",
                SourceUrl = "stub:catchup",
                TargetDirectory = dir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-catchup", Title = "Song", Artist = "A",
                        DownloadStatus = "downloaded", CurrentPath = trackPath, Position = 0,
                    },
                },
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-old", Name = "Old", SourceId = "pl-catchup",
                OutputTarget = "M3uFile", M3uOutputPath = m3u, Schedule = "Daily",
                CreatedAt = DateTime.UtcNow.AddDays(-2),
            });
            await db.SaveChangesAsync();
        }

        var runner = new SyncJobRunner(_factory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(), NullLogger<SyncJobRunner>.Instance);
        var sp = Provider(
            (typeof(IDbContextFactory<MelodyBridgeDbContext>), _factory),
            (typeof(ISyncJobRunner), runner));

        await RunUntilAsync(new FastSyncJobScheduler(sp), async () =>
        {
            await using var db = _factory.CreateDbContext();
            var job = await db.SyncJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == "job-old");
            return job?.LastRunAt is not null;
        }, TimeSpan.FromSeconds(15));

        Assert.That(File.Exists(m3u), Is.True, "the missed run catches up on the first poll");
    }

    [Test]
    public void Composition_HostsTheSyncJobScheduler()
    {
        // The wiring is part of the fix: a service class nobody starts
        // would leave cron expressions as dead data again.
        var services = new ServiceCollection();
        services.AddMelodyBridge();

        Assert.That(services.Any(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(SyncJobSchedulingBackgroundService)),
            Is.True, "the app must host the sync job scheduler");
    }

    private sealed class StubProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;
        public string? LastRequestedIdentifier { get; private set; }

        public bool CanHandle(string sourceIdentifier)
            => sourceIdentifier.Contains("stub:", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
        {
            LastRequestedIdentifier = sourceIdentifier;
            return Task.FromResult(new Playlist
            {
                Id = "stub-auto",
                Name = "Auto",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "T", Artist = "A",
                        SongID = new SongID(Platform.Spotify, "t1"),
                        PlatformSongID = new SongID(Platform.Spotify, "t1"),
                    },
                },
            });
        }

    }

    /// <summary>Records what the auto-sync scheduler asks the coordinator.</summary>
    private sealed class FakeDownloadTrigger : IDownloadTrigger
    {
        public bool Pending { get; set; }
        public List<string> Started { get; } = new();

        public Task<bool> HasPendingWorkAsync(string playlistId, CancellationToken ct = default)
            => Task.FromResult(Pending);

        public void Start(string playlistId) => Started.Add(playlistId);
    }

    /// <summary>
    /// Seeds one due playlist with the auto-download toggle, runs the real
    /// scheduler against the real store and returns the fake trigger.
    /// </summary>
    private async Task<FakeDownloadTrigger> RunAutoSyncAsync(
        string playlistId, bool autoDownload, bool pending, string sourceUrl)
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = playlistId,
                Name = playlistId,
                SourceUrl = sourceUrl,
                SourcePlatform = Platform.Spotify,
                ExternalId = sourceUrl,
                TargetDirectory = Path.Combine(_root, playlistId),
                ScheduleCron = "interval:10",
                AutoDownloadEnabled = autoDownload,
            });
            await db.SaveChangesAsync();
        }

        var trigger = new FakeDownloadTrigger { Pending = pending };
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        services.AddSingleton<IDownloadTrigger>(trigger);
        services.AddScoped(_ => new PlaylistStore(
            _factory,
            new ISourceProvider[] { new StubProvider() },
            new MelodyBridge.Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<MelodyBridge.Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance));
        var sp = services.BuildServiceProvider();

        await RunUntilAsync(new FastAutoSync(sp, NullLogger<AutoSyncBackgroundService>.Instance), async () =>
        {
            if (trigger.Started.Count > 0) return true;
            await using var db = _factory.CreateDbContext();
            var stamped = await db.Playlists.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playlistId);
            return stamped?.LastSyncAt is not null;
        }, TimeSpan.FromSeconds(15));

        return trigger;
    }

    [Test]
    public async Task AutoSync_RefreshesPlaylistsWhoseScheduleIsDue()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-auto",
                Name = "Auto",
                SourceUrl = "stub:auto",
                SourcePlatform = Platform.Spotify,
                ExternalId = "stub-auto",
                TargetDirectory = Path.Combine(_root, "auto-music"),
                ScheduleCron = "interval:10",
            });
            await db.SaveChangesAsync();
        }

        var provider = new StubProvider();

        // The service resolves PlaylistStore from its own scope: register
        // the real store over the shared factory and the stub source.
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        services.AddScoped(_ => new PlaylistStore(
            _factory,
            new ISourceProvider[] { provider },
            new MelodyBridge.Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<MelodyBridge.Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance));
        var sp = services.BuildServiceProvider();

        var serviceLog = new ListLogger<AutoSyncBackgroundService>();
        await RunUntilAsync(new FastAutoSync(sp, serviceLog), async () =>
        {
            // Wait for the stamped row, not the provider call: cancelling
            // mid-refresh would abort the write we are asserting.
            await using var db = _factory.CreateDbContext();
            var stamped = await db.Playlists.AsNoTracking().SingleOrDefaultAsync(p => p.Id == "pl-auto");
            return stamped?.LastSyncAt is not null;
        }, TimeSpan.FromSeconds(15));

        Assert.That(provider.LastRequestedIdentifier, Is.EqualTo("stub:auto"),
            "a due playlist is refreshed by the service");
        await using var verify = _factory.CreateDbContext();
        var row = await verify.Playlists.AsNoTracking().SingleAsync(p => p.Id == "pl-auto");
        Assert.That(row.LastSyncAt, Is.Not.Null,
            "the refresh is stamped; service log: " + string.Join(" | ", serviceLog.Messages));
    }

    [Test]
    public async Task AutoSync_AutoDownloadEnabled_WithPendingWork_StartsARun()
    {
        var trigger = await RunAutoSyncAsync("pl-dl", autoDownload: true, pending: true, sourceUrl: "stub:dl");

        Assert.That(trigger.Started, Does.Contain("pl-dl"),
            "a due refresh that leaves work starts a download run");
    }

    [Test]
    public async Task AutoSync_AutoDownloadDisabled_DoesNotStartARun()
    {
        var trigger = await RunAutoSyncAsync("pl-nodl", autoDownload: false, pending: true, sourceUrl: "stub:nodl");

        Assert.That(trigger.Started, Is.Empty,
            "the toggle is off by default: downloading stays a deliberate choice");
    }

    [Test]
    public async Task AutoSync_AutoDownloadEnabled_NoPendingWork_DoesNotStartARun()
    {
        var trigger = await RunAutoSyncAsync("pl-clean", autoDownload: true, pending: false, sourceUrl: "stub:clean");

        Assert.That(trigger.Started, Is.Empty,
            "a playlist with nothing left to fetch starts nothing");
    }

    [Test]
    public async Task ScanScheduler_ScansDueLocations_AndStampsThem()
    {
        var dir = Path.Combine(_root, "scan-me");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "one.mp3"), TestAudio.MinimalMp3());

        await using (var db = _factory.CreateDbContext())
        {
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = dir,
                ScheduleCron = "interval:10",
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        services.AddScoped(_ => _factory.CreateDbContext());
        services.AddScoped<ILibraryScanner, LibraryScanner>();
        services.AddSingleton(new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance));
        var sp = services.BuildServiceProvider();

        await RunUntilAsync(new FastScanScheduler(sp), async () =>
        {
            await using var db = _factory.CreateDbContext();
            return await db.ScanLocations.AnyAsync(l => l.LastScannedAt != null);
        }, TimeSpan.FromSeconds(15));

        await using var verify = _factory.CreateDbContext();
        var location = await verify.ScanLocations.AsNoTracking().SingleAsync();
        Assert.That(location.LastScannedAt, Is.Not.Null,
            "a due location is scanned and stamped by the service");
    }
}
