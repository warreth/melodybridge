using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
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
        public StubProvider()
            : this("stub", "Stub search", 0, 0, "wanted")
        {
        }

        public StubProvider(params string[] targets)
            : this("stub", "Stub search", 0, 0, targets)
        {
        }

        public StubProvider(
            string id, string name, int providerPriority, int targetPriority,
            params string[] targets)
        {
            Id = id;
            Name = name;
            Priority = providerPriority;
            Targets = targets
                .Select(target => new SearchProviderTarget(
                    target, SearchData.Isrc | SearchData.Text, Priority: targetPriority))
                .ToList();
        }

        public bool Throw { get; set; }
        public string? LastDownloaderId { get; private set; }
        public DownloaderSearchHit? Hit { get; set; } = new(
            "provider hit", "artist", "https://provider.test/1", TimeSpan.FromSeconds(200));

        public string Id { get; }
        public string Name { get; }
        public string Description => "test";
        public SearchData Data => SearchData.Isrc | SearchData.Text;
        public IReadOnlyList<SearchProviderTarget> Targets { get; }
        public int Priority { get; }

        public Task<DownloaderSearchHit?> SearchAsync(
            string downloaderId, TrackQuery query, CancellationToken ct = default)
        {
            LastDownloaderId = downloaderId;
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
        Assert.That(provider.LastDownloaderId, Is.EqualTo("wanted"),
            "the provider must receive the target id it reads the country from");
    }

    [Test]
    public void ProviderTargetPriority_ChoosesTheBestProvider()
    {
        var low = new StubProvider("low", "Low priority", providerPriority: 0, targetPriority: 0, "wanted");
        var high = new StubProvider("high", "High priority", providerPriority: 0, targetPriority: 10, "wanted");
        var registry = Registry(low, high);

        Assert.That(registry.ForDownloader("wanted"), Is.SameAs(high),
            "the target-specific priority is the strongest signal");
    }

    [Test]
    public void ProviderPriority_BreaksATargetPriorityTie()
    {
        var low = new StubProvider("low", "Low priority", providerPriority: 1, targetPriority: 5, "wanted");
        var high = new StubProvider("high", "High priority", providerPriority: 9, targetPriority: 5, "wanted");
        var registry = Registry(low, high);

        Assert.That(registry.ForDownloader("wanted"), Is.SameAs(high));
    }

    [Test]
    public void GetAll_OrdersProvidersByPriority()
    {
        var high = new StubProvider("high", "Zeta", providerPriority: 9, targetPriority: 0, "wanted");
        var low = new StubProvider("low", "Alpha", providerPriority: 1, targetPriority: 0, "wanted");
        var registry = Registry(low, high);

        Assert.That(registry.GetAll().Select(p => p.Id),
            Is.EqualTo(new[] { "high", "low" }),
            "the Plugins page lists the router's preference first");
    }

    [Test]
    public void DuplicateProviderIds_AreRejectedAtStartup()
    {
        var first = new StubProvider("same", "First", 0, 0, "wanted");
        var second = new StubProvider("SAME", "Second", 0, 0, "wanted");

        Assert.Throws<InvalidOperationException>(() => Registry(first, second));
    }

    [Test]
    public void OrphanTargets_AreReportedWithoutStoppingTheApp()
    {
        var provider = new StubProvider("wanted", "Ghost provider", 0, 0, "ghost");
        var registry = new SearchProviderRegistry(
            new[] { provider }, NullLogger<SearchProviderRegistry>.Instance,
            new ListDownloaderRegistry(new StubDownloader("wanted")));

        Assert.That(registry.ValidationIssues.Any(issue => issue.Contains("ghost")), Is.True);
    }

    [Test]
    public async Task ProviderSourceWithoutAnExplicitProvider_GetsTheLegacyAdapter()
    {
        var downloader = new StubDownloader(
            "qobuz", DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        var registry = new SearchProviderRegistry(
            Array.Empty<ISearchProvider>(),
            NullLogger<SearchProviderRegistry>.Instance,
            new ListDownloaderRegistry(downloader));

        var provider = registry.ForDownloader("qobuz");
        Assert.That(provider, Is.InstanceOf<LegacyDownloaderSearchProvider>());

        var hit = await registry.SearchAsync(downloader, Query());
        Assert.That(hit, Is.Not.Null);
        Assert.That(downloader.SearchCalls, Is.EqualTo(1),
            "the legacy adapter must use the source's existing search");
    }

    [Test]
    public void ExplicitProvider_WinsOverTheLegacyAdapter()
    {
        var downloader = new StubDownloader(
            "qobuz", DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        var explicitProvider = new StubProvider("explicit", "Explicit", 0, 10, "qobuz");
        var registry = new SearchProviderRegistry(
            new[] { explicitProvider },
            NullLogger<SearchProviderRegistry>.Instance,
            new ListDownloaderRegistry(downloader));

        Assert.That(registry.ForDownloader("qobuz"), Is.SameAs(explicitProvider));
        Assert.That(registry.GetAll().Any(p => p is LegacyDownloaderSearchProvider), Is.False,
            "an explicit provider means no fallback adapter is needed");
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
    public async Task SelfSearchingSource_StillUsesItsOwnSearchAll()
    {
        var downloader = new StubDownloader("wanted");
        var registry = Registry(new StubProvider());

        var hits = await registry.SearchAllAsync(downloader, Query());

        Assert.That(hits, Has.Count.EqualTo(1));
        Assert.That(downloader.SearchAllCalls, Is.EqualTo(1));
        Assert.That(downloader.SearchCalls, Is.Zero);
    }

    [Test]
    public async Task TargetMatching_IgnoresCase()
    {
        var downloader = new StubDownloader(
            "WANTED", DownloaderSearchInfo.ViaProvider(SearchData.Isrc));
        var provider = new StubProvider();
        var registry = Registry(provider);

        var hit = await registry.SearchAsync(downloader, Query());

        Assert.That(hit, Is.Not.Null);
        Assert.That(provider.LastDownloaderId, Is.EqualTo("WANTED"));
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
    public async Task ProviderBackedSourceWithoutAProvider_IsAMiss()
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
