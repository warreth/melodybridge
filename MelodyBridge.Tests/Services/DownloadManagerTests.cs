using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class DownloadManagerTests
{
    private class MockLegacyDownloader : IAsyncDownloader
    {
        private readonly bool _canHandle;
        private readonly string? _result;

        public MockLegacyDownloader(bool canHandle, string? result = null)
        {
            _canHandle = canHandle;
            _result = result;
        }

        public string Name => "MockLegacy";
        public bool CanHandle(string sourceIdentifier) => _canHandle;
        public Task<string> DownloadAsync(string sourceIdentifier, string outputDirectory, string melodyId, CancellationToken ct = default)
            => Task.FromResult(_result ?? "/path/to/mock/file.mp3");
    }

    private class MockProvider : IMusicProvider
    {
        private readonly bool _canDownload;

        public MockProvider(string id, bool canDownload = true)
        {
            Id = id;
            _canDownload = canDownload;
        }

        public string Id { get; }
        public string Name => $"Mock {Id}";
        public string Description => $"Mock provider {Id}";
        public string Icon => "🧪";
        public IReadOnlyList<Platform> SupportedPlatforms => Array.Empty<Platform>();
        public IReadOnlyList<TrackQuality> SupportedQualities => Array.Empty<TrackQuality>();

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        public Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
            => Task.FromResult<TrackInfo?>(null);

        public Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
            => Task.FromResult(_canDownload
                ? new DownloadResult(true, "/path/to/mock/provider.mp3", null, quality)
                : new DownloadResult(false, null, "Mock failure", null));
    }

    private class MockRegistry : IMusicProviderRegistry
    {
        private readonly List<IMusicProvider> _providers;

        public MockRegistry(IEnumerable<IMusicProvider> providers)
        {
            _providers = providers.ToList();
        }

        public IReadOnlyList<IMusicProvider> GetAllProviders() => _providers;
        public IMusicProvider? GetProvider(string id) => _providers.FirstOrDefault(p => p.Id == id);
        public IReadOnlyList<IMusicProvider> GetEnabledProviders() => _providers;
        public Task SetProviderEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsProviderEnabled(string id) => true;
    }

    [Test]
    public void UsesLegacyDownloader_WhenCanHandle()
    {
        var legacy = new[] { new MockLegacyDownloader(true, "/path/from/legacy.mp3") };
        var registry = new MockRegistry(Array.Empty<IMusicProvider>());
        var manager = new DownloadManager(legacy, registry, NullLogger<DownloadManager>.Instance);

        var result = manager.DownloadAsync("https://youtube.com/watch?v=abc", "/tmp", "melody-1").Result;

        Assert.That(result, Is.EqualTo("/path/from/legacy.mp3"));
    }

    [Test]
    public void FallsThroughToProviders_WhenLegacyCannotHandle()
    {
        var legacy = new[] { new MockLegacyDownloader(false) };
        var registry = new MockRegistry(new IMusicProvider[] { new MockProvider("prov-1") });
        var manager = new DownloadManager(legacy, registry, NullLogger<DownloadManager>.Instance);

        var result = manager.DownloadAsync("https://qobuz.com/track/123", "/tmp", "melody-1").Result;

        Assert.That(result, Is.EqualTo("/path/to/mock/provider.mp3"));
    }

    [Test]
    public void NoLegacyAndNoMatchingProvider_ReturnsNull()
    {
        var legacy = Array.Empty<IAsyncDownloader>();
        var registry = new MockRegistry(Array.Empty<IMusicProvider>());
        var manager = new DownloadManager(legacy, registry, NullLogger<DownloadManager>.Instance);

        var result = manager.DownloadAsync("https://example.com/track", "/tmp", "melody-1").Result;

        Assert.That(result, Is.Null);
    }

    [Test]
    public void TriesNextProvider_WhenFirstFails()
    {
        var fallbackProvider = new MockProvider("fallback", canDownload: true);
        var failingProvider = new MockProvider("failing", canDownload: false);

        var legacy = new[] { new MockLegacyDownloader(false) };
        var registry = new MockRegistry(new IMusicProvider[] { failingProvider, fallbackProvider });
        var manager = new DownloadManager(legacy, registry, NullLogger<DownloadManager>.Instance);

        var result = manager.DownloadAsync("https://tidal.com/track/999", "/tmp", "melody-2").Result;

        Assert.That(result, Is.EqualTo("/path/to/mock/provider.mp3"));
    }

    [Test]
    public void AllProvidersFail_ReturnsNull()
    {
        var failing1 = new MockProvider("fail-1", canDownload: false);
        var failing2 = new MockProvider("fail-2", canDownload: false);

        var legacy = new[] { new MockLegacyDownloader(false) };
        var registry = new MockRegistry(new IMusicProvider[] { failing1, failing2 });
        var manager = new DownloadManager(legacy, registry, NullLogger<DownloadManager>.Instance);

        var result = manager.DownloadAsync("https://deezer.com/track/555", "/tmp", "melody-3").Result;

        Assert.That(result, Is.Null);
    }

    [Test]
    public void UsesOnlyEnabledProviders()
    {
        var provider = new MockProvider("enabled-only");
        var registry = new MockRegistry(new IMusicProvider[] { provider });
        var manager = new DownloadManager(
            new[] { new MockLegacyDownloader(false) },
            registry,
            NullLogger<DownloadManager>.Instance);

        var result = manager.DownloadAsync("https://qobuz.com/track/1", "/tmp", "test").Result;

        Assert.That(result, Is.Not.Null);
    }
}
