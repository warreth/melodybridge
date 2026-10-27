using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The search router itself: provider-backed sources never call their own
/// search, self-searching sources still work, URL-only sources return
/// nothing, and a broken provider is a miss rather than a crash.
/// </summary>
[TestFixture]
public class SearchProviderRegistryTests
{
    private sealed class StubDownloader : IDownloader
    {
        public StubDownloader(string id, DownloaderSearchInfo? info = null)
        {
            Id = id;
            SearchInfo = info ?? DownloaderSearchInfo.Self(SearchData.Text);
        }

        public string Id { get; }
        public string Name => Id;
        public DownloaderSearchInfo SearchInfo { get; }
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public int SearchCalls { get; private set; }
        public int SearchAllCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                "own hit", "artist", "https://own.test/1", TimeSpan.FromSeconds(1)));
        }

        public Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            SearchAllCalls++;
            return Task.FromResult<IReadOnlyList<DownloaderSearchHit>>(new[]
            {
                new DownloaderSearchHit("own hit", "artist", "https://own.test/1", null),
            });
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
    }

    private sealed class StubProvider : ISearchProvider
    {
        public bool Throw { get; set; }
        public DownloaderSearchHit? Hit { get; set; } = new(
            "provider hit", "artist", "https://provider.test/1", TimeSpan.FromSeconds(200));

        public string Id => "stub";
        public string Name => "Stub search";
        public string Description => "test";
        public SearchData Data => SearchData.Isrc | SearchData.Text;
        public IReadOnlyList<SearchProviderTarget> Targets { get; } =
            new[] { new SearchProviderTarget("wanted", SearchData.Isrc) };

        public Task<DownloaderSearchHit?> SearchAsync(
            string downloaderId, TrackQuery query, CancellationToken ct = default)
        {
            if (Throw) throw new HttpRequestException("catalogue down");
            return Task.FromResult(Hit);
        }
    }

    private static SearchProviderRegistry Registry(params ISearchProvider[] providers)
        => new(providers, NullLogger<SearchProviderRegistry>.Instance);

    private static TrackQuery Query() => new("artist", "title", DownloadQuality.Any);

    [Test]
    public async Task ProviderBackedSource_UsesTheProviderAndNotItsOwnSearch()
    {
        var downloader = new StubDownloader(
            "wanted", DownloaderSearchInfo.ViaProvider(SearchData.Isrc));
        var provider = new StubProvider();
        var registry = Registry(provider);

        var hit = await registry.SearchAsync(downloader, Query());

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://provider.test/1"));
        Assert.That(downloader.SearchCalls, Is.Zero);
    }

    [Test]
    public async Task SelfSearchingSource_StillUsesItsOwnSearch()
    {
        var downloader = new StubDownloader("wanted");
        var registry = Registry(new StubProvider());

        var hit = await registry.SearchAsync(downloader, Query());

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://own.test/1"));
        Assert.That(downloader.SearchCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task UrlOnlySource_HasNoSearch()
    {
        var downloader = new StubDownloader("wanted", DownloaderSearchInfo.UrlOnly());
        var registry = Registry(new StubProvider());

        Assert.That(await registry.SearchAsync(downloader, Query()), Is.Null);
        Assert.That(await registry.SearchAllAsync(downloader, Query()), Is.Empty);
        Assert.That(downloader.SearchCalls, Is.Zero);
        Assert.That(downloader.SearchAllCalls, Is.Zero);
    }

    [Test]
    public async Task ProviderWithoutATarget_IsAMiss()
    {
        var downloader = new StubDownloader(
            "orphan", DownloaderSearchInfo.ViaProvider(SearchData.Isrc));
        var registry = Registry(new StubProvider());

        Assert.That(await registry.SearchAsync(downloader, Query()), Is.Null);
        Assert.That(downloader.SearchCalls, Is.Zero);
    }

    [Test]
    public async Task BrokenProvider_IsAMissNotAThrow()
    {
        var downloader = new StubDownloader(
            "wanted", DownloaderSearchInfo.ViaProvider(SearchData.Isrc));
        var registry = Registry(new StubProvider { Throw = true });

        Assert.That(await registry.SearchAsync(downloader, Query()), Is.Null);
    }

    [Test]
    public async Task SearchAll_IncludesTheProvidersRunnerUps()
    {
        var downloader = new StubDownloader(
            "wanted", DownloaderSearchInfo.ViaProvider(SearchData.Isrc));
        var provider = new StubProvider
        {
            Hit = new DownloaderSearchHit(
                "provider hit", "artist", "https://provider.test/1", null,
                Alternatives: new[]
                {
                    new DownloaderSearchHit("alt", "artist", "https://provider.test/2", null),
                }),
        };
        var registry = Registry(provider);

        var hits = await registry.SearchAllAsync(downloader, Query());

        Assert.That(hits.Select(h => h.SourceUrl),
            Is.EquivalentTo(new[] { "https://provider.test/1", "https://provider.test/2" }));
    }
}
