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
            Is.EquivalentTo(new[] { "tidal", "rhythmax" }));
        Assert.That(searches.ForDownloader("tidal"), Is.SameAs(provider));
    }

    [Test]
    public void EveryIsrcCapableLegacySource_HasAProviderRoute()
    {
        using var host = AppDownloadHost.Create();
        var searches = host.Services.GetRequiredService<ISearchProviderRegistry>();

        foreach (var id in new[] { "qobuz", "deemix", "jumo", "monochrome", "lucida" })
        {
            var provider = searches.ForDownloader(id);
            Assert.That(provider, Is.Not.Null, $"{id} must have a search provider");
            Assert.That(provider, Is.InstanceOf<LegacyDownloaderSearchProvider>(),
                $"{id} still uses the legacy source-side search adapter");
            Assert.That(provider!.Targets.Single().DownloaderId, Is.EqualTo(id));
        }
    }

    [Test]
    public void TheRealFallbackSource_IsMarkedFallbackOnly()
    {
        using var host = AppDownloadHost.Create();

        var flac = host.Registry.GetAll().OfType<FlacMusicDownloader>().Single();

        Assert.That(flac.FallbackOnly, Is.True);
        Assert.That(flac.SearchInfo.Mode, Is.EqualTo(DownloaderSearchMode.Self));
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
