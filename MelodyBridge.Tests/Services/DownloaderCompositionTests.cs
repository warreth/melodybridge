using MelodyBridge.Core;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Infrastructure.SearchProviders;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The shipped waterfall is defined once, in the app composition, and
/// these tests resolve it exactly like the server boots it. A plugin list
/// assembled inside a test can therefore never drift from the real
/// waterfall again; tests that need to change it go through
/// IDownloaderRegistry, the same enable/order state the Plugins page edits.
/// </summary>
[TestFixture]
public class DownloaderCompositionTests
{
    [Test]
    public void EnabledWaterfall_IsTheShippedRegistrationOrder()
    {
        using var host = AppDownloadHost.Create();

        var ids = host.Registry.GetEnabled().Select(d => d.Id).ToList();

        Assert.That(ids, Is.EqualTo(new[]
        {
            "soundcloud", "archiveorg", "ytdlp", "qobuz", "deemix", "tidal", "rhythmax",
            "monochrome", "slskd", "lucida", "jumo", "flacmusic",
        }), "a fresh install tries the plugins in the order AddMelodyBridge registers them");
    }

    [Test]
    public void TheRealComposition_RegistersTheTidalSearcherForBothTidalSources()
    {
        using var host = AppDownloadHost.Create();

        var searches = host.Services.GetRequiredService<ISearchProviderRegistry>();
        var provider = searches.ForDownloader("rhythmax");

        Assert.That(provider, Is.Not.Null, "the Tidal provider must be registered in the real app");
        Assert.That(provider!.Targets.Select(t => t.DownloaderId),
            Is.EquivalentTo(new[] { "tidal", "rhythmax", "lucida" }));
        Assert.That(searches.ForDownloader("tidal"), Is.SameAs(provider));
    }

    [Test]
    public void EveryIsrcCapableSource_HasItsOwnProvider()
    {
        using var host = AppDownloadHost.Create();
        var searches = host.Services.GetRequiredService<ISearchProviderRegistry>();

        var expected = new Dictionary<string, Type>
        {
            ["qobuz"] = typeof(QobuzSearchProvider),
            ["deemix"] = typeof(DeemixSearchProvider),
            ["monochrome"] = typeof(MonochromeSearchProvider),
        };
        foreach (var (id, type) in expected)
        {
            var provider = searches.ForDownloader(id);
            Assert.That(provider, Is.Not.Null, $"{id} must have a search provider");
            Assert.That(provider, Is.InstanceOf(type),
                $"{id} uses its own catalogue provider");
            Assert.That(provider!.Targets.Single(t => t.DownloaderId == id).DownloaderId, Is.EqualTo(id));
        }

        var qobuz = searches.GetAll().OfType<QobuzSearchProvider>().Single();
        Assert.That(qobuz.Targets.Any(t => t.DownloaderId == "jumo"), Is.True,
            "the Qobuz provider maps its ids to a Jumo URL");
        Assert.That(qobuz.Targets.Any(t => t.DownloaderId == "lucida"), Is.True,
            "Lucida consumes the official Qobuz URL");
        Assert.That(searches.GetAll().Any(p => p is JumoSearchProvider), Is.True,
            "the no-account Jumo searcher remains the fallback");

        Assert.That(searches.ForDownloader("lucida"),
            Is.InstanceOf<QobuzSearchProvider>(),
            "Lucida has no searcher; the best official provider supplies its URL");
        Assert.That(searches.GetAll().Any(p => p is LucidaSearchProvider), Is.False,
            "Lucida is URL-only in the real composition");
        Assert.That(searches.GetAll().Any(p => p is LegacyDownloaderSearchProvider), Is.False,
            "no built-in source still needs the legacy adapter");

        var order = searches.ProviderFirstOrder();
        Assert.That(order.Keys.Take(7), Is.EqualTo(new[]
        {
            "qobuz", "tidal", "rhythmax", "deemix", "lucida", "jumo", "monochrome",
        }), "official sources first, then the ripper, then the unofficial catalogues");
    }

    [Test]
    public void EveryBuiltInSource_IsProviderBacked()
    {
        using var host = AppDownloadHost.Create();
        var searches = host.Services.GetRequiredService<ISearchProviderRegistry>();

        foreach (var downloader in host.Registry.GetAll())
        {
            Assert.That(downloader.SearchInfo.Mode, Is.EqualTo(DownloaderSearchMode.Provider),
                $"{downloader.Id} must be URL-only");
            Assert.That(searches.ForDownloader(downloader.Id), Is.Not.Null,
                $"{downloader.Id} must have a provider");
        }
    }

    [Test]
    public void TheFiveSelfSearchSources_AreProvidersInTheSelfTier()
    {
        using var host = AppDownloadHost.Create();
        var searches = host.Services.GetRequiredService<ISearchProviderRegistry>();
        var selfSearch = new[] { "archiveorg", "flacmusic", "slskd", "soundcloud", "ytdlp" };

        Assert.That(searches.GetAll(), Has.Count.EqualTo(10),
            "each built-in source has its own provider, including the five self-search ones");
        Assert.That(searches.ProviderFirstOrder().Keys, Does.Not.Contain("ytdlp"),
            "self-search sources keep the user's order instead of a rank");
        Assert.That(searches.ProviderFirstOrder().Keys, Does.Not.Contain("flacmusic"));

        foreach (var id in selfSearch)
        {
            var provider = searches.ForDownloader(id);
            Assert.That(provider, Is.Not.Null, $"{id} must route through its own provider");
            Assert.That(provider!.Tier, Is.EqualTo(SearchTier.SelfSearch));
            var target = provider.Targets.Single(t =>
                string.Equals(t.DownloaderId, id, StringComparison.OrdinalIgnoreCase));
            Assert.That(target.Tier, Is.EqualTo(SearchTier.SelfSearch),
                $"{id} must sit in the self-search tier");
        }
    }

    [Test]
    public void TheRealFallbackSource_IsMarkedFallbackOnly()
    {
        using var host = AppDownloadHost.Create();

        var flac = host.Registry.GetAll().OfType<FlacMusicDownloader>().Single();

        Assert.That(flac.FallbackOnly, Is.True);
        Assert.That(flac.SearchInfo.Mode, Is.EqualTo(DownloaderSearchMode.Provider),
            "fallback-only is an ordering rule, not a search mode");
        Assert.That(flac.SearchInfo.Data, Is.EqualTo(SearchData.Text));
    }

    [Test]
    public void EveryShippedPlugin_IsEnabledAndDeclaresWhatItCanProduce()
    {
        using var host = AppDownloadHost.Create();

        var plugins = host.Registry.GetAll();
        var enabled = host.Registry.GetEnabled();

        Assert.That(plugins.Select(p => p.Id), Is.Unique, "a plugin id is the registry key");
        Assert.That(enabled, Has.Count.EqualTo(plugins.Count),
            "a fresh install enables every shipped plugin");
        foreach (var plugin in plugins)
        {
            Assert.That(plugin.Capabilities.Containers, Is.Not.Empty,
                $"{plugin.Id} must declare the containers it can produce");
            Assert.That(plugin.Capabilities.SupportsLossless || plugin.Capabilities.SupportsLossy,
                Is.True, $"{plugin.Id} must declare lossless and/or lossy support");
        }
    }
}
