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
        private readonly int _delayMs;
        public SlowDownloader(int delayMs = 150) => _delayMs = delayMs;

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
            try { await Task.Delay(_delayMs, ct); } catch (TaskCanceledException) { }
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

        /// <summary>The melody ids this downloader was asked for, in order.</summary>
        public List<string> Requested { get; } = new();

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
            lock (Requested) Requested.Add(melodyId ?? "");
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

    /// <summary>
    /// Fails one track once with a transient database error. The failure
    /// comes from above the pipeline: the row is not even claimed. The
    /// coordinator has to retry it and keep the run alive.
    /// </summary>
    private sealed class FlakyPlaylistStore : PlaylistStore
    {
        private readonly int _failTrackId;
        private bool _failed;

        public FlakyPlaylistStore(
            IDbContextFactory<MelodyBridgeDbContext> factory, IDownloadManager downloader,
            int failTrackId)
            : base(factory, Array.Empty<ISourceProvider>(), downloader,
                NullLogger<PlaylistStore>.Instance)
        {
            _failTrackId = failTrackId;
        }

        /// <summary>
        /// Fails the first attempt of one track with a transient database
        /// error. The retry of that track goes through to the real
        /// pipeline.
        /// </summary>
        public override async Task<string> DownloadTrackAsync(
            int trackId, CancellationToken ct = default, bool avoidLastProvider = false)
        {
            lock (this)
            {
                if (!_failed && trackId == _failTrackId)
                {
                    _failed = true;
                    throw new InvalidOperationException("transient database busy");
                }
            }
            return await base.DownloadTrackAsync(trackId, ct, avoidLastProvider);
        }
    }

    private ServiceProvider MakeServices(
        IDownloader downloader, PlaylistStore? store = null, LiveTrackClaims? claims = null)
    {
        claims ??= new LiveTrackClaims();
        store ??= new PlaylistStore(_factory,
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListRegistry(downloader),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            claims: claims);
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        services.AddSingleton(store);
        services.AddSingleton(new SettingsStore(_factory));
        // One registry for the app: the store and the coordinator must see
        // the same live claims.
        services.AddSingleton<ITrackClaimRegistry>(claims);
        return services.BuildServiceProvider();
    }

    private static DownloadCoordinator MakeCoordinator(ServiceProvider services)
        => new(services, NullLogger<DownloadCoordinator>.Instance,
            services.GetRequiredService<ITrackClaimRegistry>());

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
    public async Task Run_SurvivesProviderCancellationAndTransientStoreFailure()
    {
        var id = await SeedPlaylistAsync(6);

        // Two different tracks, two different failures. The first track
        // fails once in the store: a transient database error from above
        // the pipeline. The second track is cancelled by the provider
        // itself: that cancellation comes from below the pipeline, so the
        // pipeline has to write the failure and pass it on, and the
        // coordinator has to retry the track.
        int storeTrackId;
        string cancelledMelodyId;
        await using (var seed = _factory.CreateDbContext())
        {
            var seeded = await seed.Tracks.AsNoTracking()
                .Where(t => t.PlaylistEntityId == id)
                .OrderBy(t => t.Position)
                .ToListAsync();
            storeTrackId = seeded[0].Id;
            cancelledMelodyId = seeded[1].MelodyId!;
        }

        var downloader = new SlowDownloader();
        var real = new DownloadManager(new ListRegistry(downloader),
            NullLogger<DownloadManager>.Instance);
        var manager = new CancellingManager(real, cancelledMelodyId);
        var store = new FlakyPlaylistStore(_factory, manager, storeTrackId);
        var services = MakeServices(downloader, store);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 6, TimeSpan.FromSeconds(30));

        await using var db = _factory.CreateDbContext();
        var rows = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == id)
            .ToListAsync();
        var statuses = string.Join(", ", rows.Select(r => $"{r.Title}={r.DownloadStatus}/{r.DownloadError}"));
        var run = coordinator.RunFor(id)!;
        Assert.Multiple(() =>
        {
            Assert.That(run.State, Is.EqualTo(DownloadRunState.Finished));
            Assert.That(run.Done, Is.EqualTo(6),
                $"both one-off failures were retried and every track was attempted: {statuses}");
            Assert.That(run.QueueLength, Is.Zero);
        });
        Assert.That(rows, Has.Count.EqualTo(6));
        Assert.That(rows.Any(r => r.DownloadStatus == "in_progress"), Is.False,
            "a failed attempt must never leave a claim behind");
        Assert.That(rows.All(r => r.DownloadStatus == "downloaded"), Is.True,
            $"every track arrived after its retry: {statuses}");
        Assert.That(manager.Attempts, Is.EqualTo(7),
            "the cancelled track took two attempts, every other track one");
    }

    [Test]
    public async Task Start_RecoversAClaimLeftByAnEarlierDeadRun()
    {
        var id = await SeedPlaylistAsync(1);
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.PlaylistEntityId == id);
            track.DownloadStatus = "in_progress";
            await db.SaveChangesAsync();
        }

        var downloader = new SlowDownloader();
        var coordinator = MakeCoordinator(MakeServices(downloader));
        coordinator.Start(id);

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 1, TimeSpan.FromSeconds(20));

        await using var fresh = _factory.CreateDbContext();
        var saved = await fresh.Tracks.AsNoTracking().SingleAsync(t => t.PlaylistEntityId == id);
        Assert.That(saved.DownloadStatus, Is.EqualTo("downloaded"),
            "a redownload must reclaim a row a dead run left in progress");
    }

    [Test]
    public async Task Start_DoesNotStealAClaimFromALiveSingleTrackDownload()
    {
        // A single-track download is running: it holds the row, and a
        // batch that starts now must leave that row alone. Clearing it
        // would put the same track in the batch's work list, and two
        // downloads would write the same file.
        var id = await SeedPlaylistAsync(2);
        var downloader = new BlockingDownloader();
        var claims = new LiveTrackClaims();
        var store = new PlaylistStore(_factory,
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListRegistry(downloader), NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            claims: claims);
        var services = MakeServices(downloader, store, claims);
        var coordinator = MakeCoordinator(services);

        int firstTrackId;
        string firstMelodyId;
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.AsNoTracking()
                .Where(t => t.PlaylistEntityId == id)
                .OrderBy(t => t.Position)
                .FirstAsync();
            firstTrackId = track.Id;
            firstMelodyId = track.MelodyId!;
        }

        // The single-track download claims the row and starts.
        var single = Task.Run(() => store.DownloadTrackAsync(firstTrackId));
        await WaitForAsync(() => downloader.Requested.Count == 1, TimeSpan.FromSeconds(20));

        // A batch starts while that download is still running.
        coordinator.Start(id);
        await WaitForAsync(() => downloader.Requested.Count >= 2, TimeSpan.FromSeconds(20));
        // Let the batch settle: a second attempt on the same track would
        // show up here.
        await WaitForStableAsync(() => downloader.Requested.Count,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));

        int requestsForTheClaimedTrack;
        lock (downloader.Requested)
            requestsForTheClaimedTrack = downloader.Requested.Count(id => id == firstMelodyId);
        Assert.That(requestsForTheClaimedTrack, Is.EqualTo(1),
            "the batch left the track alone: the live download keeps it");

        downloader.Release();
        await single;
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished,
            TimeSpan.FromSeconds(30));

        await using var fresh = _factory.CreateDbContext();
        var saved = await fresh.Tracks.AsNoTracking()
            .SingleAsync(t => t.Id == firstTrackId);
        Assert.That(saved.DownloadStatus, Is.EqualTo("downloaded"),
            "the single-track download finished the track");
    }

    [Test]
    public async Task Run_RetriesATrackWhoseProviderCancelledTheRequest()
    {
        // A provider that cancels its own request is a track failure, not
        // a reason to stop the worker, and the track gets a second
        // attempt. The cancellation has to reach the coordinator for that
        // to happen: the pipeline must not swallow it.
        var id = await SeedPlaylistAsync(1);
        var downloader = new SlowDownloader();
        var real = new DownloadManager(new ListRegistry(downloader),
            NullLogger<DownloadManager>.Instance);
        var manager = new CancellingManager(real);
        var store = new PlaylistStore(_factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance);
        var services = MakeServices(downloader, store);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 1, TimeSpan.FromSeconds(30));

        await using var db = _factory.CreateDbContext();
        var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.PlaylistEntityId == id);
        Assert.Multiple(() =>
        {
            Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"),
                "the provider cancellation was retried and the track arrived");
            Assert.That(manager.Attempts, Is.EqualTo(2),
                "the track was attempted twice: the cancel and the retry");
        });
    }

    [Test]
    public async Task Run_ReportsAnEtaBasedOnItsOwnProgress()
    {
        // A playlist that was mostly downloaded before: the ETA must be
        // paced by this run, not by the whole playlist. Mixing the two
        // collapses the estimate to a moment that has already passed.
        const int total = 24;
        const int alreadyDownloaded = 20;
        var id = await SeedPlaylistAsync(total);
        await using (var db = _factory.CreateDbContext())
        {
            var tracks = await db.Tracks.Where(t => t.PlaylistEntityId == id)
                .OrderBy(t => t.Position).ToListAsync();
            foreach (var track in tracks.Take(alreadyDownloaded))
            {
                track.DownloadStatus = "downloaded";
                track.CurrentPath = Path.Combine(_dir, $"{track.MelodyId}.mp3");
            }
            await db.SaveChangesAsync();
        }

        var downloader = new SlowDownloader(delayMs: 2000);
        var coordinator = MakeCoordinator(MakeServices(downloader));
        coordinator.Start(id);

        // The first tracks of this run are done: there is a pace.
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= alreadyDownloaded + 2,
            TimeSpan.FromSeconds(60));

        var run = coordinator.RunFor(id)!;
        Assert.That(run.EtaUtc, Is.Not.Null,
            "a run that finished a track can say when it will be done");
        var now = DateTime.UtcNow;
        var elapsed = now - run.StartedAtUtc;
        Assert.That(run.EtaUtc!.Value, Is.GreaterThanOrEqualTo(now.Add(elapsed / 2)),
            "the ETA is paced by the tracks this run still has to do, not by the whole playlist");

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished,
            TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// A manager whose first attempt for one track ends the way a
    /// provider that cancels its own request does: the cancellation
    /// travels on from below the pipeline, so the pipeline writes the
    /// failure and the caller can retry the track.
    /// </summary>
    private sealed class CancellingManager(IDownloadManager inner, string? cancelMelodyId = null) : IDownloadManager
    {
        private int _remaining = 1;
        public int Attempts;

        public Task<string?> DownloadTrackAsync(TrackQuery query, string outputDirectory, string melodyId,
            DownloadFallback? fallback = null, string? avoidPluginId = null, CancellationToken ct = default,
            DownloadJournal? journal = null)
        {
            Interlocked.Increment(ref Attempts);
            if ((cancelMelodyId is null || cancelMelodyId == melodyId)
                && Interlocked.Decrement(ref _remaining) >= 0)
                throw new OperationCanceledException("the provider cancelled this request");
            return inner.DownloadTrackAsync(query, outputDirectory, melodyId, fallback, avoidPluginId, ct);
        }

        public Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId,
            CancellationToken ct = default)
            => inner.DownloadAsync(sourceUrl, outputDirectory, melodyId, ct);

        public Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(TrackQuery query, CancellationToken ct = default)
            => inner.SearchCandidatesAsync(query, ct);

        public Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(TrackQuery query, Action<DownloadCandidate>? onCandidate, CancellationToken ct = default)
            => inner.SearchCandidatesAsync(query, onCandidate, ct);

        public Task<string?> DownloadCandidateAsync(DownloadCandidate candidate, TrackQuery query, string outputDirectory, string melodyId, CancellationToken ct = default)
            => inner.DownloadCandidateAsync(candidate, query, outputDirectory, melodyId, ct);

        public IReadOnlyList<DownloadProgress> SnapshotProgress() => inner.SnapshotProgress();
        public void ReportProgress(DownloadProgress progress) => inner.ReportProgress(progress);
        public string? LastPluginId(string melodyId) => inner.LastPluginId(melodyId);
        public string? LastAttemptedPluginId(string melodyId) => inner.LastAttemptedPluginId(melodyId);
        public string? LastFailure(string melodyId) => inner.LastFailure(melodyId);
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
