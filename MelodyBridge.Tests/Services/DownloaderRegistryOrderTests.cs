using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Honest tests for the waterfall order persistence: a real SQLite
/// database, real registry, real plugin state rows. No mocks on the
/// persistence path.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DownloaderRegistryOrderTests
{
    private IDbContextFactory<MelodyBridgeDbContext> _dbFactory = null!;
    private DownloaderRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source=file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;

        _dbFactory = new InlineFactory(options);
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        _registry = new DownloaderRegistry(
            new IDownloader[]
            {
                new StubDownloader("soundcloud", "SoundCloud"),
                new StubDownloader("archiveorg", "Internet Archive"),
                new StubDownloader("ytdlp", "yt-dlp (YouTube)"),
            },
            _dbFactory,
            NullLogger<DownloaderRegistry>.Instance);
    }

    [Test]
    public void GetEnabled_InitialOrder_FollowsRegistration()
    {
        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids, Is.EqualTo(new[] { "soundcloud", "archiveorg", "ytdlp" }));
    }

    [Test]
    public async Task SetOrderAsync_ReordersWaterfallAndPersists()
    {
        await _registry.SetOrderAsync(new[] { "ytdlp", "soundcloud", "archiveorg" });

        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids, Is.EqualTo(new[] { "ytdlp", "soundcloud", "archiveorg" }),
            "SetOrderAsync must define the exact waterfall order");

        // Persisted: a fresh registry over the same DB reads the same order.
        var fresh = new DownloaderRegistry(
            new IDownloader[]
            {
                new StubDownloader("soundcloud", "SoundCloud"),
                new StubDownloader("archiveorg", "Internet Archive"),
                new StubDownloader("ytdlp", "yt-dlp (YouTube)"),
            },
            _dbFactory,
            NullLogger<DownloaderRegistry>.Instance);
        var persistedIds = fresh.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(persistedIds, Is.EqualTo(new[] { "ytdlp", "soundcloud", "archiveorg" }),
            "the order must survive a restart");
    }

    [Test]
    public async Task MoveUpSimulation_SwappingNeighborsKeepsDensePriorities()
    {
        // The exact operation the Downloads page arrows perform.
        var order = _registry.GetEnabled().Select(d => d.Id).ToList();
        (order[1], order[2]) = (order[2], order[1]); // ytdlp moves up above archiveorg
        await _registry.SetOrderAsync(order);

        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids, Is.EqualTo(new[] { "soundcloud", "ytdlp", "archiveorg" }));

        // Priorities are dense 0..2, never drifting apart.
        var priorities = new List<int>();
        foreach (var id in ids)
            priorities.Add(await _registry.GetPriorityAsync(id));
        Assert.That(priorities, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public async Task SetOrderAsync_UnknownIdsAreAppended()
    {
        await _registry.SetOrderAsync(new[] { "ytdlp", "nonexistent-plugin" });

        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids[0], Is.EqualTo("ytdlp"));
        Assert.That(ids, Does.Not.Contain("nonexistent-plugin"),
            "unknown ids must not appear in the waterfall");
    }

    private sealed class InlineFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InlineFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class StubDownloader : IDownloader
    {
        public StubDownloader(string id, string name) { Id = id; Name = name; }
        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
    }
}
