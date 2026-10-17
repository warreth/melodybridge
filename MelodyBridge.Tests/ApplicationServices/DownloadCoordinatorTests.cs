using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// DownloadCoordinator behavior against a real SQLite database and the
/// real PlaylistStore: pause/resume/cancel transitions, live snapshot
/// counts, and clean finish when everything is downloaded.
/// </summary>
[TestFixture]
public class DownloadCoordinatorTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private string _dir = null!;

    /// <summary>Slow downloader so the run takes a while: lets us pause/cancel mid-run.</summary>
    private sealed class SlowDownloader : IDownloader
    {
        public string Id => "slow";
        public string Name => "Slow (test)";
        public string Description => "";
        public int Downloads;
        /// <summary>Attempts that entered DownloadAsync, finished or not.</summary>
        public int Started;
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult(new DownloaderSearchHit(query.Title, query.Artist, "https://slow.example/" + query.Title, TimeSpan.FromSeconds(1)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Started);
            try { await Task.Delay(150, ct); } catch (TaskCanceledException) { }
            Interlocked.Increment(ref Downloads);
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            // Real MP3 bytes: the download gate ffprobes finished files.
            await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3(), ct);
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    /// <summary>
    /// Blocks inside DownloadAsync until released: pins what a cancel
    /// does to a download that is already in flight.
    /// </summary>
    private sealed class BlockingDownloader : IDownloader
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started;
        public int Completed;

        public string Id => "blocking";
        public string Name => "Blocking (test)";
        public string Description => "";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult(new DownloaderSearchHit(query.Title, query.Artist,
                "https://blocking.example/1", TimeSpan.FromSeconds(1)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Started);
            await _release.Task.WaitAsync(ct); // throws OperationCanceledException on cancel
            Interlocked.Increment(ref Completed);
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3(), CancellationToken.None);
            return new DownloaderDownloadResult(true, path, null);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ListRegistry : IDownloaderRegistry
    {
        private readonly IDownloader[] _downloaders;
        public ListRegistry(params IDownloader[] downloaders) => _downloaders = downloaders;
        public IReadOnlyList<IDownloader> GetAll() => _downloaders;
        public IDownloader? Get(string id) => _downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => _downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-coord-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-coord-{Guid.NewGuid():N}");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { }
    }

    private async Task<string> SeedPlaylistAsync(int trackCount)
    {
        await using var db = _factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Name = "Coord Test",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TargetDirectory = _dir,
            Tracks = Enumerable.Range(0, trackCount).Select(i => new TrackEntity
            {
                MelodyId = $"mb-coord-{i}",
                Title = $"Track {i}",
                Artist = "Artist",
                Position = i,
                DownloadStatus = "pending",
            }).ToList(),
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return playlist.Id;
    }

    private ServiceProvider MakeServices(IDownloader downloader)
    {
        var store = new PlaylistStore(_factory,
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListRegistry(downloader),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        services.AddSingleton(store);
        services.AddSingleton(new SettingsStore(_factory));
        return services.BuildServiceProvider();
    }

    private static DownloadCoordinator MakeCoordinator(ServiceProvider services)
        => new(services, NullLogger<DownloadCoordinator>.Instance);

    [Test]
    public async Task Run_DownloadsEverything_AndFinishes()
    {
        var id = await SeedPlaylistAsync(3);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 3, TimeSpan.FromSeconds(20));

        var run = coordinator.RunFor(id)!;
        Assert.That(run.State, Is.EqualTo(DownloadRunState.Finished));
        Assert.That(run.Done, Is.EqualTo(3));
        Assert.That(run.Failed, Is.EqualTo(0));
        Assert.That(downloader.Downloads, Is.EqualTo(3));
        Assert.That(Directory.EnumerateFiles(_dir).Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task HasPendingWorkAsync_CountsPendingAndFailedTracks()
    {
        var id = await SeedPlaylistAsync(2);
        var coordinator = MakeCoordinator(MakeServices(new SlowDownloader()));

        Assert.That(await coordinator.HasPendingWorkAsync(id), Is.True,
            "never downloaded tracks are pending work");

        // One downloaded, one failed: still work (failures retry each run).
        await using (var db = _factory.CreateDbContext())
        {
            var tracks = await db.Tracks.Where(t => t.PlaylistEntityId == id)
                .OrderBy(t => t.Position).ToListAsync();
            tracks[0].DownloadStatus = "downloaded";
            tracks[1].DownloadStatus = "failed";
            await db.SaveChangesAsync();
        }
        Assert.That(await coordinator.HasPendingWorkAsync(id), Is.True, "failed tracks count as work");

        // Everything downloaded: nothing left.
        await using (var db = _factory.CreateDbContext())
        {
            var tracks = await db.Tracks.Where(t => t.PlaylistEntityId == id).ToListAsync();
            foreach (var track in tracks) track.DownloadStatus = "downloaded";
            await db.SaveChangesAsync();
        }
        Assert.That(await coordinator.HasPendingWorkAsync(id), Is.False);

        Assert.That(await coordinator.HasPendingWorkAsync("pl-missing"), Is.False,
            "a playlist that does not exist has no work");
    }

    [Test]
    public async Task Pause_BetweenTracks_ThenResume()
    {
        var id = await SeedPlaylistAsync(4);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= 1, TimeSpan.FromSeconds(20));

        coordinator.Pause(id);
        var run = coordinator.RunFor(id)!;
        Assert.That(run.State, Is.EqualTo(DownloadRunState.Paused));

        // The in-flight track may finish (by design: pause is cooperative),
        // but no new attempt may start. Waiting for the start count to stop
        // moving is deterministic; a fixed delay raced the slow downloader
        // under full-suite load.
        var started = await WaitForStableAsync(() => downloader.Started,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        await Task.Delay(600);
        Assert.That(downloader.Started, Is.EqualTo(started), "no new downloads while paused");

        coordinator.Start(id); // resume path
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(20));
        Assert.That(coordinator.RunFor(id)!.Done, Is.EqualTo(4));
    }

    [Test]
    public async Task Cancel_StopsTheRun_Cooperatively()
    {
        var id = await SeedPlaylistAsync(10);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= 1, TimeSpan.FromSeconds(20));

        coordinator.Cancel(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(20));

        Assert.That(downloader.Downloads, Is.LessThan(10), "cancel must stop before all 10 are done");
        Assert.That(coordinator.RunFor(id)!.Done + coordinator.RunFor(id)!.Failed,
            Is.LessThan(10), "the snapshot reflects the partial run");
    }

    [Test]
    public async Task Cancel_WhileATrackIsInFlight_ReleasesTheClaim()
    {
        var id = await SeedPlaylistAsync(3);
        var downloader = new BlockingDownloader();
        var coordinator = MakeCoordinator(MakeServices(downloader));

        coordinator.Start(id);
        await WaitForAsync(() => downloader.Started >= 1, TimeSpan.FromSeconds(20));

        coordinator.Cancel(id);
        downloader.Release(); // let the in-flight call observe the cancelled token

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished,
            TimeSpan.FromSeconds(20));

        await using var db = _factory.CreateDbContext();
        var statuses = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == id)
            .Select(t => t.DownloadStatus)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Does.Not.Contain("in_progress"),
                "a cancelled in-flight track must not stay claimed");
            Assert.That(statuses.Count(s => s == "failed"), Is.GreaterThanOrEqualTo(1),
                "the cancelled attempt is recorded honestly");
            Assert.That(downloader.Completed, Is.EqualTo(0),
                "the blocked track never produced a file");
        });
    }

    [Test]
    public async Task Run_LogsWhenFinished()
    {
        var id = await SeedPlaylistAsync(1);
        var logger = new ListLogger<DownloadCoordinator>();
        var coordinator = new DownloadCoordinator(MakeServices(new SlowDownloader()), logger);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished,
            TimeSpan.FromSeconds(20));

        Assert.That(logger.Messages.Any(m => m.Contains("finished") && m.Contains("1 of 1")),
            Is.True, "a finished run reports its result to the log");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Test]
    public async Task Restart_AfterFinish_RunsAgain()
    {
        var id = await SeedPlaylistAsync(2);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(20));
        Assert.That(coordinator.RunFor(id)!.Done, Is.EqualTo(2));

        // Add new pending tracks, start again: a finished run must restart.
        await using (var db = _factory.CreateDbContext())
        {
            var track = new TrackEntity
            {
                MelodyId = "mb-coord-new",
                Title = "New Track",
                Artist = "Artist",
                Position = 9,
                DownloadStatus = "pending",
                PlaylistEntityId = id,
            };
            db.Tracks.Add(track);
            await db.SaveChangesAsync();
        }

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.Done == 3
            && coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(20));
        Assert.That(downloader.Downloads, Is.EqualTo(3));
    }

    /// <summary>Downloader that tracks how many downloads run at once.</summary>
    private sealed class InFlightDownloader : IDownloader
    {
        public int Downloads;
        public int MaxInFlight;
        private int _inFlight;

        public string Id => "inflight";
        public string Name => "In-Flight (test)";
        public string Description => "";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(query.Title, query.Artist, "https://inflight.example/1", TimeSpan.FromSeconds(1)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int current, observed;
            do
            {
                current = Volatile.Read(ref MaxInFlight);
                observed = Math.Max(current, now);
            } while (observed > current
                     && Interlocked.CompareExchange(ref MaxInFlight, observed, current) != current);

            try
            {
                await Task.Delay(300, ct);
                Interlocked.Increment(ref Downloads);
                Directory.CreateDirectory(outputDirectory);
                var path = Path.Combine(outputDirectory, melodyId + ".mp3");
                // Real MP3 bytes: the download gate ffprobes finished files.
                await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3(), ct);
                return new DownloaderDownloadResult(true, path, null);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [Test]
    public async Task MaxConcurrent_LimitsParallelDownloads()
    {
        // 6 tracks, workers capped at 2: the in-flight high-water mark
        // must never exceed 2, and everything still downloads.
        var id = await SeedPlaylistAsync(6);
        var downloader = new InFlightDownloader();
        var services = MakeServices(downloader);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("download_max_concurrent", "2");
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 6, TimeSpan.FromSeconds(30));

        Assert.That(downloader.Downloads, Is.EqualTo(6), "all tracks still download");
        Assert.That(downloader.MaxInFlight, Is.LessThanOrEqualTo(2),
            "the download_max_concurrent setting must cap parallel downloads");
        Assert.That(downloader.MaxInFlight, Is.GreaterThanOrEqualTo(2),
            "with 6 pending tracks and a 300ms downloader both workers should overlap");
    }

    [Test]
    public async Task MaxConcurrent_OneWorker_StaysSequential()
    {
        var id = await SeedPlaylistAsync(3);
        var downloader = new InFlightDownloader();
        var services = MakeServices(downloader);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("download_max_concurrent", "1");
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 3, TimeSpan.FromSeconds(30));

        Assert.That(downloader.MaxInFlight, Is.EqualTo(1), "setting 1 restores sequential downloads");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Waits until the observed count has not moved for <paramref name="stableFor"/>,
    /// then returns it. Deterministic where a fixed delay is not: the caller
    /// asserts that the count stays put afterwards.
    /// </summary>
    private static async Task<int> WaitForStableAsync(
        Func<int> value, TimeSpan stableFor, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var current = value();
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            var next = value();
            if (next != current)
            {
                current = next;
                stableSince = DateTime.UtcNow;
                continue;
            }
            if (DateTime.UtcNow - stableSince >= stableFor) return current;
        }
        return current;
    }
}
