using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class DownloadManagerExtendedTests
{
    private IDbContextFactory<MelodyBridgeDbContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"DownloadMgrExt_{Guid.NewGuid()}")
            .Options;
        return new InMemoryDbContextFactory(options);
    }

    private class InMemoryDbContextFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    [Test]
    public async Task DownloadAsync_EmptyLegacyDownloaders_TriesProviders()
    {
        var factory = CreateDbContextFactory();
        var legacyDownloaders = Array.Empty<IAsyncDownloader>();
        var providers = new IMusicProvider[]
        {
            new MockMusicProvider("mock1", true, "/tmp/test.mp3"),
        };
        var registry = new MusicProviderRegistry(providers, factory,
            NullLogger<MusicProviderRegistry>.Instance);

        var manager = new DownloadManager(legacyDownloaders, registry,
            NullLogger<DownloadManager>.Instance);

        var result = await manager.DownloadAsync(
            "https://example.com/track",
            "/tmp/output",
            "test-melody-id");

        Assert.That(result, Is.EqualTo("/tmp/test.mp3"));
    }

    [Test]
    public async Task DownloadAsync_AllProvidersDisabled_ReturnsNull()
    {
        var factory = CreateDbContextFactory();
        var legacyDownloaders = Array.Empty<IAsyncDownloader>();
        var provider = new MockMusicProvider("disabled-provider", true, "/tmp/test.mp3");
        var providers = new IMusicProvider[] { provider };
        var registry = new MusicProviderRegistry(providers, factory,
            NullLogger<MusicProviderRegistry>.Instance);

        // Disable the provider
        await registry.SetProviderEnabledAsync("disabled-provider", false);

        var manager = new DownloadManager(legacyDownloaders, registry,
            NullLogger<DownloadManager>.Instance);

        var result = await manager.DownloadAsync(
            "https://example.com/track",
            "/tmp/output",
            "test-melody-id");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task DownloadAsync_ProviderThrowsException_FallsToNext()
    {
        var factory = CreateDbContextFactory();
        var legacyDownloaders = Array.Empty<IAsyncDownloader>();
        var providers = new IMusicProvider[]
        {
            new MockMusicProvider("failing", false, null, throws: true),
            new MockMusicProvider("working", true, "/tmp/fallback.mp3"),
        };
        var registry = new MusicProviderRegistry(providers, factory,
            NullLogger<MusicProviderRegistry>.Instance);

        var manager = new DownloadManager(legacyDownloaders, registry,
            NullLogger<DownloadManager>.Instance);

        var result = await manager.DownloadAsync(
            "https://example.com/track",
            "/tmp/output",
            "test-melody-id");

        Assert.That(result, Is.EqualTo("/tmp/fallback.mp3"));
    }

    [Test]
    public async Task DownloadAsync_NoDownloaderAvailable_ReturnsNull()
    {
        var factory = CreateDbContextFactory();
        var legacyDownloaders = Array.Empty<IAsyncDownloader>();
        var providers = Array.Empty<IMusicProvider>();
        var registry = new MusicProviderRegistry(providers, factory,
            NullLogger<MusicProviderRegistry>.Instance);

        var manager = new DownloadManager(legacyDownloaders, registry,
            NullLogger<DownloadManager>.Instance);

        var result = await manager.DownloadAsync(
            "https://example.com/track",
            "/tmp/output",
            "test-melody-id");

        Assert.That(result, Is.Null);
    }
}

/// <summary>
/// A minimal mock provider for testing DownloadManager behavior.
/// </summary>
internal class MockMusicProvider : IMusicProvider
{
    private readonly bool _success;
    private readonly string? _filePath;
    private readonly bool _throws;

    public MockMusicProvider(string id, bool success, string? filePath, bool throws = false)
    {
        Id = id;
        Name = id;
        _success = success;
        _filePath = filePath;
        _throws = throws;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description => "Mock provider for testing";
    public string Icon => "🧪";
    public IReadOnlyList<Platform> SupportedPlatforms => new[] { Platform.Qobuz };
    public IReadOnlyList<TrackQuality> SupportedQualities => Array.Empty<TrackQuality>();

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

    public Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
        => Task.FromResult<TrackInfo?>(null);

    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        await Task.Yield();
        if (_throws) throw new InvalidOperationException("Mock failure");
        return _success
            ? new DownloadResult(true, _filePath, null, quality)
            : new DownloadResult(false, null, "Mock failure", null);
    }
}
