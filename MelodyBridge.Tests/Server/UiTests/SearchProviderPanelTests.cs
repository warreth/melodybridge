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
        {
            Targets = targets
                .Select(id => new SearchProviderTarget(id, SearchData.Isrc | SearchData.Text))
                .ToList();
        }

        public string Id => "tidal";
        public string Name => "Tidal ISRC search";
        public string Description => "Looks up the recording by ISRC and returns a track URL.";
        public SearchData Data => SearchData.Isrc | SearchData.Text;
        public IReadOnlyList<SearchProviderTarget> Targets { get; }

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
        Assert.That(cut.Markup, Does.Contain("Sources that search themselves"));
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
