using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests;

/// <summary>
/// Integration-level tests that verify multiple components work together
/// without external network calls or file I/O.
/// </summary>
[TestFixture]
public class IntegrationTests
{
    private IDbContextFactory<MelodyBridgeDbContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"Integration_{Guid.NewGuid()}")
            .Options;

        return new InMemoryDbContextFactory(options);
    }

    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"IntegrationDb_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private class InMemoryDbContextFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Downloader registry lifecycle: registration, defaults, toggle, priority.
    /// </summary>
    [Test]
    public async Task Registry_DownloaderLifecycle()
    {
        var factory = CreateDbContextFactory();
        var downloaders = new IDownloader[]
        {
            new StubDownloader("ytdlp", "yt-dlp (YouTube)"),
            new StubDownloader("test2", "Second Plugin"),
        };

        var registry = new DownloaderRegistry(downloaders, factory,
            NullLogger<DownloaderRegistry>.Instance);

        // All plugins should be registered
        Assert.That(registry.GetAll(), Has.Exactly(2).Items);

        // All should be enabled by default
        foreach (var p in downloaders)
            Assert.That(registry.IsEnabled(p.Id), Is.True);

        // Disable one
        await registry.SetEnabledAsync("test2", false);
        Assert.That(registry.IsEnabled("test2"), Is.False);
        Assert.That(registry.GetEnabled(), Has.Exactly(1).Items);

        // Re-enable
        await registry.SetEnabledAsync("test2", true);
        Assert.That(registry.IsEnabled("test2"), Is.True);
        Assert.That(registry.GetEnabled(), Has.Exactly(2).Items);

        // Priority ordering
        await registry.SetPriorityAsync("test2", 0);
        Assert.That(registry.GetEnabled()[0].Id, Is.EqualTo("test2"));
    }

    /// <summary>
    /// DownloadManager waterfall: unavailable plugins are skipped, the first
    /// successful plugin wins.
    /// </summary>
    [Test]
    public async Task DownloadManager_Waterfall_SkipsUnavailableAndUsesFirstHit()
    {
        var factory = CreateDbContextFactory();
        var downloaders = new IDownloader[]
        {
            new StubDownloader("unavailable", "Never Works", available: false),
            new StubDownloader("second", "Second Plugin", available: true, downloadPath: null),
            new StubDownloader("third", "Third Plugin", available: true,
                downloadPath: "/tmp/waterfall-hit.mp3"),
        };

        var registry = new DownloaderRegistry(downloaders, factory,
            NullLogger<DownloaderRegistry>.Instance);

        var manager = new Application.Services.DownloadManager(
            registry,
            NullLogger<Application.Services.DownloadManager>.Instance);

        var path = await manager.DownloadAsync("https://example.com/x", "/tmp", "mid");

        Assert.That(path, Is.EqualTo("/tmp/waterfall-hit.mp3"),
            "waterfall must skip the unavailable plugin and the failing one, then use the third");
    }

    /// <summary>
    /// DownloadManager metadata flow: search hit then download.
    /// </summary>
    [Test]
    public async Task DownloadManager_TrackFlow_SearchesThenDownloads()
    {
        var factory = CreateDbContextFactory();
        var downloaders = new IDownloader[]
        {
            new StubDownloader("nosearch", "No Search", available: true, searchHit: null),
            new StubDownloader("full", "Full Plugin", available: true,
                searchHit: new DownloaderSearchHit("Hit", "Artist", "https://example.com/hit", null),
                downloadPath: "/tmp/track-flow.mp3"),
        };

        var registry = new DownloaderRegistry(downloaders, factory,
            NullLogger<DownloaderRegistry>.Instance);
        var manager = new Application.Services.DownloadManager(
            registry,
            NullLogger<Application.Services.DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Some Title", "/tmp", "mid2");

        Assert.That(path, Is.EqualTo("/tmp/track-flow.mp3"));
        var progress = manager.SnapshotProgress().Single(p => p.MelodyId == "mid2");
        Assert.That(progress.Status, Is.EqualTo("done"));
        Assert.That(progress.Plugin, Is.EqualTo("Full Plugin"));
    }

    /// <summary>
    /// Tests that the database context works with track entities.
    /// </summary>
    [Test]
    public async Task Database_TrackEntityLifecycle()
    {
        using var db = CreateDbContext();

        var track = new TrackEntity
        {
            MelodyId = "integration-test-id",
            ExternalId = "spotify-123",
            ExternalPlatform = "Spotify",
            Title = "Integration Track",
            Artist = "Test Artist",
            MediaType = "MP3",
            CurrentPath = "/music/test.mp3",
        };

        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var loaded = await db.Tracks.FirstOrDefaultAsync(t => t.MelodyId == "integration-test-id");
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Title, Is.EqualTo("Integration Track"));
            Assert.That(loaded.Artist, Is.EqualTo("Test Artist"));
            Assert.That(loaded.CurrentPath, Is.EqualTo("/music/test.mp3"));
            Assert.That(loaded.ExternalId, Is.EqualTo("spotify-123"));
        });
    }

    /// <summary>
    /// Tests that ProviderStateRow (plugin enable/priority state) persists.
    /// </summary>
    [Test]
    public async Task Database_ProviderStateLifecycle()
    {
        using var db = CreateDbContext();

        var state = new ProviderStateRow
        {
            ProviderId = "test-plugin",
            IsEnabled = false,
            Priority = 3,
        };

        db.ProviderStates.Add(state);
        await db.SaveChangesAsync();

        var loaded = await db.ProviderStates.FindAsync("test-plugin");
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.IsEnabled, Is.False);
            Assert.That(loaded.Priority, Is.EqualTo(3));
        });

        // Update
        loaded.IsEnabled = true;
        await db.SaveChangesAsync();
        var reloaded = await db.ProviderStates.FindAsync("test-plugin");
        Assert.That(reloaded!.IsEnabled, Is.True);
    }

    /// <summary>
    /// Minimal IDownloader stub for waterfall tests.
    /// </summary>
    private sealed class StubDownloader(
        string id,
        string name,
        bool available = true,
        DownloaderSearchHit? searchHit = null,
        string? downloadPath = null) : IDownloader
    {
        public string Id => id;
        public string Name => name;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(available);

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult(searchHit);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId, CancellationToken ct = default)
            => Task.FromResult(downloadPath is null
                ? new DownloaderDownloadResult(false, null, "stub failure")
                : new DownloaderDownloadResult(true, downloadPath, null));
    }
}
