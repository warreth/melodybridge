using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Shared;
using MelodyBridge.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The Plugins page's search-routing panel: providers, the sources they can
/// search, the data they read, and the sources that deliberately search
/// themselves or accept only a URL.
/// </summary>
[TestFixture]
[Category("UI")]
public class SearchProviderPanelTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private sealed class StubDownloader : IDownloader
    {
        public StubDownloader(string id, string name, DownloaderSearchInfo info, bool fallbackOnly = false)
        {
            Id = id;
            Name = name;
            SearchInfo = info;
            FallbackOnly = fallbackOnly;
        }

        public string Id { get; }
        public string Name { get; }
        public DownloaderSearchInfo SearchInfo { get; }
        public bool FallbackOnly { get; }
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
    }

    private sealed class StubProvider : ISearchProvider
    {
        public StubProvider(params string[] targets)
            : this("tidal", "Tidal ISRC search", 0, targets)
        {
        }

        public StubProvider(string id, string name, int priority, params string[] targets)
        {
            Id = id;
            Name = name;
            Priority = priority;
            Targets = targets
                .Select(target => new SearchProviderTarget(target, SearchData.Isrc | SearchData.Text))
                .ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => "Looks up the recording by ISRC and returns a track URL.";
        public SearchData Data => SearchData.Isrc | SearchData.Text;
        public IReadOnlyList<SearchProviderTarget> Targets { get; }
        public int Priority { get; }
        public SearchTier Tier { get; init; } = SearchTier.Official;

        public Task<DownloaderSearchHit?> SearchAsync(
            string downloaderId, TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
    }

    private void Register(ISearchProvider provider, params IDownloader[] downloaders)
    {
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry(downloaders));
        _ctx.Services.AddSingleton<ISearchProviderRegistry>(
            new SearchProviderRegistry(new[] { provider }, NullLogger<SearchProviderRegistry>.Instance));
    }

    [Test]
    public void Panel_ShowsProvidersTargetsAndData()
    {
        Register(
            new StubProvider("rhythmax"),
            new StubDownloader("rhythmax", "Tidal (Rhythmax)",
                DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text)),
            new StubDownloader("ytdlp", "yt-dlp (YouTube)",
                DownloaderSearchInfo.Self(SearchData.Text, "title search")),
            new StubDownloader("flacmusic", "FLAC Music (CN)",
                DownloaderSearchInfo.Self(SearchData.Text, "title-only fallback"),
                fallbackOnly: true),
            new StubDownloader("doubledouble", "DoubleDouble",
                DownloaderSearchInfo.UrlOnly("direct URLs only")));

        var cut = _ctx.Render<SearchProviderPanel>();

        Assert.That(cut.Find("details.search-provider-map").HasAttribute("open"), Is.False,
            "the routing map is collapsed until the user asks for it");
        Assert.That(cut.FindAll(".search-provider"), Has.Count.EqualTo(1));
        var provider = cut.Find(".search-provider");
        Assert.That(provider.TextContent, Does.Contain("Tidal ISRC search"));
        var badges = provider.QuerySelectorAll(".search-data-badge")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.That(badges, Does.Contain("ISRC"));
        Assert.That(badges, Does.Contain("title + artist"));
        Assert.That(provider.QuerySelector(".search-target")?.GetAttribute("href"),
            Is.EqualTo("#plugin-rhythmax"));
        Assert.That(provider.TextContent, Does.Contain("Tidal (Rhythmax)"));
        var selfRows = cut.FindAll(".search-self-row").Select(r => r.TextContent.Trim()).ToList();
        Assert.That(selfRows.Any(t => t.Contains("yt-dlp (YouTube)")), Is.True);
        Assert.That(selfRows.Any(t => t.Contains("DoubleDouble")), Is.True);
        Assert.That(selfRows.Any(t => t.Contains("FLAC Music (CN)")
                                     && t.Contains("fallback", StringComparison.OrdinalIgnoreCase)),
            Is.True, "the real fallback source gets its badge");
        Assert.That(cut.Markup, Does.Contain("Third-party sources that search themselves"));
        Assert.That(cut.Markup, Does.Contain("Direct URL only"));
    }

    [Test]
    public void Panel_ShowsTheNoProviderHintWhenEveryProviderIsMissing()
    {
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry());
        _ctx.Services.AddSingleton<ISearchProviderRegistry>(
            new SearchProviderRegistry(Array.Empty<ISearchProvider>(),
                NullLogger<SearchProviderRegistry>.Instance));

        var cut = _ctx.Render<SearchProviderPanel>();

        Assert.That(cut.Markup, Does.Contain("No search provider is registered"));
    }

    [Test]
    public void Panel_WarnsAboutAProviderBackedSourceWithNoProviderTarget()
    {
        Register(
            new StubProvider("tidal"),
            new StubDownloader("rhythmax", "Tidal (Rhythmax)",
                DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text)));

        var cut = _ctx.Render<SearchProviderPanel>();

        Assert.That(cut.FindAll(".search-provider-warning"), Has.Count.EqualTo(1));
        Assert.That(cut.Find(".search-provider-warning").TextContent,
            Does.Contain("Tidal (Rhythmax)"));
    }

    [Test]
    public void Panel_MarksALegacyCatalogueProvider()
    {
        var downloader = new StubDownloader("qobuz", "Qobuz",
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry(downloader));
        _ctx.Services.AddSingleton<ISearchProviderRegistry>(
            new SearchProviderRegistry(Array.Empty<ISearchProvider>(),
                NullLogger<SearchProviderRegistry>.Instance,
                new ListDownloaderRegistry(downloader)));

        var cut = _ctx.Render<SearchProviderPanel>();

        var provider = cut.Find(".search-provider");
        Assert.That(provider.TextContent, Does.Contain("legacy"));
        Assert.That(provider.TextContent, Does.Contain("Searches the Qobuz catalogue, ISRC first."));
    }

    [Test]
    public void Panel_MarksAnUnofficialCatalogue()
    {
        var downloader = new StubDownloader("wanted", "Wanted",
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        var provider = new StubProvider("unofficial", "Unofficial", 0, "wanted")
        {
            Tier = SearchTier.UnofficialCatalog,
        };
        Register(provider, downloader);

        var cut = _ctx.Render<SearchProviderPanel>();

        var badge = cut.Find(".search-tier-badge");
        Assert.That(badge.TextContent.Trim(), Is.EqualTo("unofficial"));
        Assert.That(badge.ClassList, Does.Contain("unofficial"));
    }

    [Test]
    public void Panel_MarksASelfSearchProvider()
    {
        var downloader = new StubDownloader("ytdlp", "yt-dlp (YouTube)",
            DownloaderSearchInfo.ViaProvider(SearchData.Text));
        var provider = new StubProvider("youtube-search", "YouTube search", 0, "ytdlp")
        {
            Tier = SearchTier.SelfSearch,
        };
        Register(provider, downloader);

        var cut = _ctx.Render<SearchProviderPanel>();

        var badge = cut.Find(".search-tier-badge");
        Assert.That(badge.TextContent.Trim(), Is.EqualTo("self"));
        Assert.That(badge.ClassList, Does.Contain("self"));
    }

    [Test]
    public void Panel_MarksThePrimaryProviderWhenASourceHasTwo()
    {
        var downloader = new StubDownloader("rhythmax", "Tidal (Rhythmax)",
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        var low = new StubProvider("low", "Low priority", 1, "rhythmax");
        var high = new StubProvider("high", "High priority", 9, "rhythmax");
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry(downloader));
        _ctx.Services.AddSingleton<ISearchProviderRegistry>(
            new SearchProviderRegistry(new[] { low, high },
                NullLogger<SearchProviderRegistry>.Instance));

        var cut = _ctx.Render<SearchProviderPanel>();

        var cards = cut.FindAll(".search-provider");
        var highCard = cards.Single(c =>
            c.QuerySelector(".search-provider-title strong")!.TextContent.Contains("High priority"));
        var lowCard = cards.Single(c =>
            c.QuerySelector(".search-provider-title strong")!.TextContent.Contains("Low priority"));
        Assert.That(highCard.QuerySelector(".search-target-primary"), Is.Not.Null,
            "the provider the router prefers is marked");
        Assert.That(lowCard.QuerySelector(".search-target-primary"), Is.Null,
            "the runner-up is not marked primary");
    }

    [Test]
    public void Panel_RendersValidationIssuesAsAList()
    {
        var downloader = new StubDownloader("wanted", "Wanted",
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry(downloader));
        _ctx.Services.AddSingleton<ISearchProviderRegistry>(
            new SearchProviderRegistry(new[] { new StubProvider("ghost") },
                NullLogger<SearchProviderRegistry>.Instance,
                new ListDownloaderRegistry(downloader)));

        var cut = _ctx.Render<SearchProviderPanel>();

        var issues = cut.FindAll(".search-provider-issues li")
            .Select(item => item.TextContent.Trim()).ToList();
        Assert.That(issues, Has.Some.Contains("ghost"),
            "each routing problem is its own list item");
    }

    [Test]
    public void Panel_MarksADisabledTarget()
    {
        var downloader = new StubDownloader("rhythmax", "Tidal (Rhythmax)",
            DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text));
        var registry = new Mock<IDownloaderRegistry>();
        registry.Setup(r => r.GetAll()).Returns(new[] { downloader });
        registry.Setup(r => r.Get("rhythmax")).Returns(downloader);
        registry.Setup(r => r.IsEnabled("rhythmax")).Returns(false);
        _ctx.Services.AddSingleton(registry.Object);
        _ctx.Services.AddSingleton<ISearchProviderRegistry>(
            new SearchProviderRegistry(new[] { new StubProvider("rhythmax") },
                NullLogger<SearchProviderRegistry>.Instance));

        var cut = _ctx.Render<SearchProviderPanel>();

        var target = cut.Find(".search-target");
        Assert.That(target.ClassList, Does.Contain("is-disabled"));
        Assert.That(target.TextContent, Does.Contain("off"));
    }

    [Test]
    public void Panel_RendersNothingWhenNoSearchServiceIsRegistered()
    {
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry(
            new StubDownloader("ytdlp", "yt-dlp", DownloaderSearchInfo.Self(SearchData.Text))));

        var cut = _ctx.Render<SearchProviderPanel>();

        Assert.That(cut.Markup, Does.Not.Contain("search-provider-map"));
    }
}
