using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The visible download queue: QueueFor returns the pending/failed titles in
/// Position order for the live run, the queue shrinks as tracks complete, the
/// snapshot carries its length, and an ETA appears once a pace can be
/// measured. Real SQLite, real PlaylistStore, real DownloadCoordinator.
/// </summary>
[TestFixture]
public class DownloadQueueTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private string _dir = null!;

    /// <summary>Downloader slow enough to observe the mid-run queue.</summary>
    private sealed class SlowDownloader : IDownloader
    {
        public string Id => "slow";
        public string Name => "Slow (test)";
        public string Description => "";
        public int Downloads;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult(new DownloaderSearchHit(title, artist, "https://slow.example/" + title, TimeSpan.FromSeconds(1)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            try { await Task.Delay(150, ct); } catch (TaskCanceledException) { }
            Interlocked.Increment(ref Downloads);
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, melodyId + ".mp3");
            // Real MP3 bytes: the download gate ffprobes every finished
            // file, so a one-letter text file would fail the integrity
            // check and never count as done.
            await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3(), ct);
            return new DownloaderDownloadResult(true, path, null);
        }
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
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-queue-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-queue-{Guid.NewGuid():N}");
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

    private async Task<string> SeedPlaylistAsync(int trackCount, string titlePrefix = "Track ")
    {
        await using var db = _factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Name = "Queue Test",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TargetDirectory = _dir,
            Tracks = Enumerable.Range(0, trackCount).Select(i => new TrackEntity
            {
                MelodyId = $"mb-queue-{i}",
                Title = titlePrefix + i,
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

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task QueueFor_ReturnsPendingTitles_InPositionOrder()
    {
        var id = await SeedPlaylistAsync(6);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        // Wait until at least one track completed but the run is still going:
        // the queue must show exactly the remaining titles, in order.
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= 1
            && coordinator.RunFor(id)?.State != DownloadRunState.Finished, TimeSpan.FromSeconds(20));

        var queue = coordinator.QueueFor(id);
        Assert.That(queue, Is.Not.Empty, "mid-run the queue holds the remaining titles");
        // Position order: "Track 1" before "Track 2" etc., regardless of
        // which worker claimed which track first.
        var ordered = queue.Select(t => int.Parse(t.Split(' ')[1])).ToList();
        Assert.That(ordered, Is.EqualTo(ordered.OrderBy(n => n).ToList()),
            "queue must follow playlist Position order");

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished
            && coordinator.RunFor(id)?.Done == 6, TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task QueueFor_ShrinksAsTracksComplete_AndSnapshotCarriesLength()
    {
        var id = await SeedPlaylistAsync(5);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.QueueLength > 0, TimeSpan.FromSeconds(20));
        var earlyLength = coordinator.RunFor(id)!.QueueLength;
        Assert.That(earlyLength, Is.GreaterThanOrEqualTo(4), "five pending tracks minus in-flight work");

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(30));
        Assert.That(coordinator.RunFor(id)!.QueueLength, Is.EqualTo(0), "nothing left in the queue when finished");
        Assert.That(coordinator.QueueFor(id), Is.Empty);
    }

    [Test]
    public async Task EtaUtc_IsNullAtStart_AndSetOncePaceIsMeasured()
    {
        var id = await SeedPlaylistAsync(4);
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State != null, TimeSpan.FromSeconds(20));

        // Before any track completes there is no pace: no ETA yet.
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= 1, TimeSpan.FromSeconds(20));
        var run = coordinator.RunFor(id)!;
        Assert.That(run.EtaUtc, Is.Not.Null,
            "once at least one track completed, an ETA must be measurable");
        Assert.That(run.EtaUtc, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-1)),
            "the ETA must lie in the future");

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(30));
        Assert.That(coordinator.RunFor(id)!.EtaUtc, Is.Null,
            "nothing remaining: no ETA when the run finishes");
    }

    [Test]
    public void QueueFor_UnknownPlaylist_ReturnsEmpty()
    {
        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        Assert.That(coordinator.QueueFor("no-such-playlist"), Is.Empty);
    }

    [Test]
    public async Task QueueFor_IncludesFailedTracks_ForRetryOrder()
    {
        var id = await SeedPlaylistAsync(3);
        // Mark track 1 failed: it stays claimable, so the queue must keep it.
        await using (var db = _factory.CreateDbContext())
        {
            var failedTrack = await db.Tracks.FirstAsync(t => t.PlaylistEntityId == id && t.Position == 1);
            failedTrack.DownloadStatus = "failed";
            await db.SaveChangesAsync();
        }

        var downloader = new SlowDownloader();
        var services = MakeServices(downloader);
        var coordinator = MakeCoordinator(services);

        coordinator.Start(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.QueueLength >= 3, TimeSpan.FromSeconds(20));

        var queue = coordinator.QueueFor(id);
        Assert.That(queue.Count, Is.GreaterThanOrEqualTo(3),
            "pending + failed tracks all belong to the visible queue");

        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(30));
    }
}
