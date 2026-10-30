using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The manager must ask the search router, not the downloader directly.
/// That is the contract that keeps a URL+ISRC source free of its own text
/// search even when a provider is the only thing that knows the URL.
/// </summary>
[TestFixture]
public class DownloadManagerSearchRoutingTests
{
    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        _tempDirs.Clear();
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class RecordingRouter : ISearchProviderRegistry
    {
        public int SearchCalls { get; private set; }
        public int SearchAllCalls { get; private set; }
        public DownloaderSearchHit? Hit { get; set; } = new(
            "provider hit", "artist", "https://provider.test/track", TimeSpan.FromSeconds(180));

        public IReadOnlyList<ISearchProvider> GetAll() => Array.Empty<ISearchProvider>();
        public ISearchProvider? ForDownloader(string downloaderId) => null;

        public Dictionary<string, int> Order { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> ProviderFirstOrder() => Order;

        public Task<DownloaderSearchHit?> SearchAsync(
            IDownloader downloader, TrackQuery query, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(Hit);
        }

        public Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            IDownloader downloader, TrackQuery query, CancellationToken ct = default)
        {
            SearchAllCalls++;
            return Task.FromResult<IReadOnlyList<DownloaderSearchHit>>(
                Hit is null ? Array.Empty<DownloaderSearchHit>() : new[] { Hit });
        }
    }

    private sealed class ProviderDownloader : IDownloader
    {
        public string Id => "rhythmax";
        public string Name => "Rhythmax";
        public DownloaderSearchInfo SearchInfo =>
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text);
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public int OwnSearchCalls { get; private set; }
        public int OwnSearchAllCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            OwnSearchCalls++;
            throw new InvalidOperationException("the source must not search itself");
        }

        public Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            OwnSearchAllCalls++;
            throw new InvalidOperationException("the source must not search itself");
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            var path = Path.Combine(outputDirectory, $"routed-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private sealed class OrderedDownloader(string id, bool succeed, List<string> attempts)
        : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public DownloaderSearchInfo SearchInfo =>
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text);
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(
            TrackQuery query, CancellationToken ct = default)
            => throw new InvalidOperationException("the provider route must search");

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            attempts.Add(Id);
            if (!succeed)
                return Task.FromResult(new DownloaderDownloadResult(false, null, "down"));
            var path = Path.Combine(outputDirectory, $"{Id}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private static DownloadManager Manager(
        IDownloader downloader, ISearchProviderRegistry router)
        => new(new ListDownloaderRegistry(downloader),
            NullLogger<DownloadManager>.Instance, searches: router);

    [Test]
    public async Task Waterfall_OfficialFirstOrder_TriesTheRankedSourceFirst()
    {
        var attempts = new List<string>();
        var first = new OrderedDownloader("first", succeed: true, attempts);
        var second = new OrderedDownloader("second", succeed: false, attempts);
        var router = new RecordingRouter();
        router.Order["second"] = 0;
        router.Order["first"] = 1;
        var manager = new DownloadManager(
            new ListDownloaderRegistry(first, second),
            NullLogger<DownloadManager>.Instance, searches: router);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("artist", "title", DownloadQuality.Any), NewDir(), "melody-order");

        Assert.That(path, Is.Not.Null, manager.LastFailure("melody-order"));
        Assert.That(attempts, Is.EqualTo(new[] { "second", "first" }),
            "the provider-first rank decides before the user's source order");
    }

    [Test]
    public async Task Waterfall_UsesTheRouterInsteadOfTheSourcesOwnSearch()
    {
        var downloader = new ProviderDownloader();
        var router = new RecordingRouter();
        var manager = Manager(downloader, router);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("artist", "title", DownloadQuality.Any),
            NewDir(), "melody-1");

        Assert.That(path, Is.Not.Null, manager.LastFailure("melody-1"));
        Assert.That(router.SearchCalls, Is.EqualTo(1));
        Assert.That(downloader.OwnSearchCalls, Is.Zero,
            "the provider route must bypass the source's own search entirely");
        Assert.That(File.Exists(path!), Is.True);
    }

    private sealed class CuratedDownloader : IDownloader
    {
        public string Id => "curated";
        public string Name => "Curated";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(
            TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                query.Title, query.Artist, "https://curated.test/1", TimeSpan.FromSeconds(180)));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            var path = Path.Combine(outputDirectory, $"curated-{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }

    [Test]
    public async Task FallbackOnlySource_IsNeverTriedBeforeACuratedSource()
    {
        var flacHandler = new CountingHandler();
        var flac = new FlacMusicDownloader(
            new FlacMusicClient(new HttpClient(flacHandler),
                NullLogger<FlacMusicClient>.Instance),
            new StubPluginConfig(),
            NullLogger<FlacMusicDownloader>.Instance);
        var curated = new CuratedDownloader();
        var manager = new DownloadManager(
            new ListDownloaderRegistry(flac, curated),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("artist", "title", DownloadQuality.Any),
            NewDir(), "melody-fallback");

        Assert.That(path, Is.Not.Null, manager.LastFailure("melody-fallback"));
        Assert.That(flacHandler.Requests, Is.Zero,
            "the fallback source must not even be probed while a curated source works");
        Assert.That(manager.LastPluginId("melody-fallback"), Is.EqualTo("curated"));
    }

    [Test]
    public async Task ManualPickerSearch_AlsoGoesThroughTheRouter()
    {
        var downloader = new ProviderDownloader();
        var router = new RecordingRouter();
        var manager = Manager(downloader, router);

        var outcome = await manager.SearchCandidatesDetailedAsync(
            new TrackQuery("artist", "title", DownloadQuality.Any));

        Assert.That(outcome.Candidates, Has.Count.EqualTo(1));
        Assert.That(outcome.Candidates[0].SourceUrl, Is.EqualTo("https://provider.test/track"));
        Assert.That(router.SearchAllCalls, Is.EqualTo(1));
        Assert.That(downloader.OwnSearchAllCalls, Is.Zero);
        Assert.That(downloader.OwnSearchCalls, Is.Zero,
            "the manual picker must not fall through to the source's own search");
    }
}
