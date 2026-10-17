using MelodyBridge.Core;
using MelodyBridge.Tests.Infrastructure;

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
            "soundcloud", "archiveorg", "ytdlp", "qobuz", "deemix", "tidal",
            "monochrome", "slskd", "lucida", "jumo", "doubledouble",
        }), "a fresh install tries the plugins in the order AddMelodyBridge registers them");
        // DoubleDouble trails on purpose: it reports itself unavailable
        // while its captcha gate is up, so it must never sit in front of a
        // working source.
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
