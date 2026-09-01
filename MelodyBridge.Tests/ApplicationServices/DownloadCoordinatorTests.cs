using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
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
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            await File.WriteAllTextAsync(path, "x", ct);
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

    private PlaylistStore MakeStore(SlowDownloader downloader)
        => new(_factory,
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListRegistry(downloader),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private static DownloadCoordinator MakeCoordinator(PlaylistStore store, TestSqliteFactory factory)
        => new(store, factory, NullLogger<DownloadCoordinator>.Instance);

    [Test]
    public async Task Run_DownloadsEverything_AndFinishes()
    {
        var id = await SeedPlaylistAsync(3);
        var downloader = new SlowDownloader();
        var coordinator = MakeCoordinator(MakeStore(downloader), _factory);

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
    public async Task Pause_BetweenTracks_ThenResume()
    {
        var id = await SeedPlaylistAsync(4);
        var downloader = new SlowDownloader();
        var coordinator = MakeCoordinator(MakeStore(downloader), _factory);

        coordinator.Start(id);
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= 1, TimeSpan.FromSeconds(20));

        coordinator.Pause(id);
        var run = coordinator.RunFor(id)!;
        Assert.That(run.State, Is.EqualTo(DownloadRunState.Paused));

        // The in-flight track may finish (by design: pause is cooperative).
        await Task.Delay(400);
        var settledAt = downloader.Downloads;
        await Task.Delay(600);
        Assert.That(downloader.Downloads, Is.EqualTo(settledAt), "no new downloads while paused");

        coordinator.Start(id); // resume path
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(20));
        Assert.That(coordinator.RunFor(id)!.Done, Is.EqualTo(4));
    }

    [Test]
    public async Task Cancel_StopsTheRun_Cooperatively()
    {
        var id = await SeedPlaylistAsync(10);
        var downloader = new SlowDownloader();
        var coordinator = MakeCoordinator(MakeStore(downloader), _factory);

        coordinator.Start(id);
        await WaitForAsync(() => (coordinator.RunFor(id)?.Done ?? 0) >= 1, TimeSpan.FromSeconds(20));

        coordinator.Cancel(id);
        await WaitForAsync(() => coordinator.RunFor(id)?.State == DownloadRunState.Finished, TimeSpan.FromSeconds(20));

        Assert.That(downloader.Downloads, Is.LessThan(10), "cancel must stop before all 10 are done");
        Assert.That(coordinator.RunFor(id)!.Done + coordinator.RunFor(id)!.Failed,
            Is.LessThan(10), "the snapshot reflects the partial run");
    }

    [Test]
    public async Task Restart_AfterFinish_RunsAgain()
    {
        var id = await SeedPlaylistAsync(2);
        var downloader = new SlowDownloader();
        var coordinator = MakeCoordinator(MakeStore(downloader), _factory);

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

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }
}
