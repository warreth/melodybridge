using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Lucida;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The quality router: the manifest is the only input. Plugins that cannot
/// serve a request are excluded before any network call, and when a request
/// has constraints the plugins that *control* the quality run before the
/// ones that merely might deliver it. No plugin name, no priority table
/// and no magic band decides anything; the spies prove exclusions by never
/// being searched.
/// </summary>
[TestFixture]
public class QualityRouterTests
{
    /// <summary>Minimal plugin double: counts searches, always fails, never downloads.</summary>
    private sealed class CapabilityDownloader(
        string id,
        PluginCapabilities caps,
        List<string>? searchSpy = null) : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public PluginCapabilities Capabilities => caps;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            searchSpy?.Add(id);
            return Task.FromResult<DownloaderSearchHit?>(null);
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "spy never downloads"));
    }

    /// <summary>Spy that answers a real hit and writes a file, to assert call order through the manager.</summary>
    private sealed class OrderingSpyDownloader(
        string id,
        PluginCapabilities caps,
        List<string> callOrder,
        string fileExt = "mp3",
        bool downloadFails = false) : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public PluginCapabilities Capabilities => caps;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            callOrder.Add(id);
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, $"https://{id}.example/1", TimeSpan.FromSeconds(1)));
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            callOrder.Add(id + ":download");
            if (downloadFails)
                return Task.FromResult(new DownloaderDownloadResult(false, null, "stub refuses"));

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{id}.{fileExt}");
            // The manager sniffs real bytes before accepting a file, so the
            // spy writes the matching container magic.
            File.WriteAllBytes(path, fileExt == "flac"
                ? "fLaC"u8.ToArray()
                : TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    // ── 1. Space Saver (cap 160): the small-file plugin survives, the FLAC-only plugin is out ──

    [Test]
    public void SpaceSaver_SmallFilePluginSurvives_LosslessOnlyExcluded()
    {
        var small = new CapabilityDownloader("small", PluginCapabilities.Lossy(AudioFormat.Mp3, 64, 320));
        var flacOnly = new CapabilityDownloader("flac-only", PluginCapabilities.LosslessSources());
        var plan = QualityRouter.Route(
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160),
            [small, flacOnly]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(), Is.EqualTo(new[] { "small" }),
            "a lossless source cannot fit under a 160 kbps cap");
        var (plugin, reason) = plan.Excluded.Single();
        Assert.That(plugin.Id, Is.EqualTo("flac-only"));
        Assert.That(reason, Does.Contain("160"),
            "the reason names the cap the plugin cannot serve under");
    }

    [Test]
    public async Task SpaceSaver_LosslessOnlyPlugin_IsNeverSearched()
    {
        var spy = new List<string>();
        var flacOnly = new CapabilityDownloader("flac-only", PluginCapabilities.LosslessSources(), spy);
        var manager = new DownloadManager(
            new ListDownloaderRegistry(flacOnly),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync(new TrackQuery("A", "T", new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160)), Path.GetTempPath(), "mel-cap");

        Assert.That(path, Is.Null);
        Assert.That(spy, Is.Empty, "an excluded plugin must never hit the network");
        Assert.That(manager.LastFailure("mel-cap"),
            Does.Contain("your quality filters excluded every plugin"),
            "the failure says the filters, not the sources, were the problem");
    }

    // ── 2. Lossless request: a lossy-only plugin is out, not merely ranked last ──

    [Test]
    public void LosslessRequest_LossyOnlyPluginIsExcluded()
    {
        // A FLAC container is not lossless content: whoever cannot serve a
        // real lossless source must not waste a download on a FLAC request.
        var lossy = new CapabilityDownloader("lossy", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320));
        var lossless = new CapabilityDownloader("lossless", PluginCapabilities.LosslessSources());
        var plan = QualityRouter.Route(new DownloadQuality(AudioFormat.Flac), [lossy, lossless]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(), Is.EqualTo(new[] { "lossless" }));
        var (plugin, reason) = plan.Excluded.Single();
        Assert.That(plugin.Id, Is.EqualTo("lossy"));
        Assert.That(reason, Does.Contain("lossless"),
            "the reason says there is no lossless source behind it");
    }

    [Test]
    public void ConstrainedRequest_ControlledPluginsRunBeforeTheMaybes()
    {
        // The user order says "maybe" first, but only the controlled plugin
        // can be asked for the requested mp3 at 320; the maybe is tried
        // after, and the request still has one class per group.
        var maybe = new CapabilityDownloader("maybe", new PluginCapabilities(
            [new QualityDeliverable(AudioFormat.Mp3, Lossless: false, Controlled: false)]));
        var controlled = new CapabilityDownloader("controlled", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320));
        var plan = QualityRouter.Route(new DownloadQuality(AudioFormat.Mp3, MinKbps: 320, MaxKbps: 320), [maybe, controlled]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(),
            Is.EqualTo(new[] { "controlled", "maybe" }),
            "whoever controls the quality is tried before whoever might deliver it");
        Assert.That(plan.Excluded, Is.Empty);
    }

    // ── 3. Strict filter (Opus, ceiling 160): mp3-only plugin excluded with reason ──

    [Test]
    public void StrictOpusCeiling160_Mp3OnlyPluginExcludedWithReason_NeverSearched()
    {
        var spy = new List<string>();
        var mp3Only = new CapabilityDownloader("mp3-only", PluginCapabilities.Lossy(AudioFormat.Mp3), spy);
        var plan = QualityRouter.Route(
            new DownloadQuality(AudioFormat.Opus, MinKbps: null, MaxKbps: 160),
            [mp3Only]);

        Assert.That(plan.Plugins, Is.Empty);
        var (plugin, reason) = plan.Excluded.Single();
        Assert.That(plugin.Id, Is.EqualTo("mp3-only"));
        Assert.That(reason, Does.Contain("Opus"),
            "the reason names the format the plugin cannot serve");
        Assert.That(spy, Is.Empty, "exclusion happens before any execution");
    }

    // ── 4. Impossible band: ceiling 96 vs plugin min 128 ──

    [Test]
    public void ImpossibleBand_Ceiling96VsMin128_ExcludedBeforeNetwork()
    {
        var spy = new List<string>();
        var fatOnly = new CapabilityDownloader("fat-only", PluginCapabilities.Lossy(AudioFormat.Mp3, 128), spy);
        var plan = QualityRouter.Route(
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 96),
            [fatOnly]);

        Assert.That(plan.Plugins, Is.Empty, "no overlap: the band is impossible for this plugin");
        var (_, reason) = plan.Excluded.Single();
        Assert.That(reason, Does.Contain("96"),
            "the reason names the ceiling the plugin cannot fit under");
        Assert.That(spy, Is.Empty, "the impossible plugin is excluded before the network");
    }

    // ── 5. All excluded: manager reports the clear LastFailure ──

    [Test]
    public async Task AllExcluded_ManagerReturnsNull_AndLastFailureNamesTheFilters()
    {
        var spy = new List<string>();
        var manager = new DownloadManager(
            new ListDownloaderRegistry(
                new CapabilityDownloader("a", PluginCapabilities.Lossy(AudioFormat.Mp3, 256), spy),
                new CapabilityDownloader("b", PluginCapabilities.LosslessSources(), spy)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync(new TrackQuery("A", "T", new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 96)), Path.GetTempPath(), "mel-all");

        Assert.That(path, Is.Null);
        Assert.That(spy, Is.Empty, "no incompatible plugin was ever searched");
        Assert.That(manager.LastFailure("mel-all"),
            Does.Contain("your quality filters excluded every plugin"));
    }

    // ── 6. Auto / no filter: user order preserved exactly ──

    [Test]
    public void AutoNoFilter_UserOrderPreservedExactly()
    {
        var plan = QualityRouter.Route(DownloadQuality.Any, new IDownloader[]
        {
            new CapabilityDownloader("third", PluginCapabilities.LosslessSources()),
            new CapabilityDownloader("first", PluginCapabilities.Lossy(AudioFormat.Mp3, null, 96)),
            new CapabilityDownloader("second", PluginCapabilities.Lossy(AudioFormat.Mp3, 256)),
        });

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(),
            Is.EqualTo(new[] { "third", "first", "second" }),
            "no filters: the router is invisible, user priority decides");
        Assert.That(plan.Excluded, Is.Empty);
    }

    // ── 7. FLAC ignores the band: a 320 cap does not exclude the lossless plugin ──

    [Test]
    public void FlacIgnoresBand_MaxKbps320_DoesNotExcludeLosslessPlugin()
    {
        var plan = QualityRouter.Route(
            new DownloadQuality(AudioFormat.Flac, MinKbps: null, MaxKbps: 320),
            [new CapabilityDownloader("flac", PluginCapabilities.LosslessSources())]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(), Is.EqualTo(new[] { "flac" }),
            "lossless band is meaningless: FLAC requests ignore MaxKbps");
        Assert.That(plan.Excluded, Is.Empty);
    }

    // ── 8. Manifests: every shipped plugin, including DAB, declares what it can do ──

    private static void AssertLossy(PluginCapabilities caps, AudioFormat container, int? min, int? max)
    {
        Assert.That(caps.Deliverables.Any(d => d.Container == container && !d.Lossless
            && d.MinKbps == min && d.MaxKbps == max && d.Controlled), Is.True,
            $"{container} declares a controlled {min}-{max} kbps level");
    }

    [Test]
    public void YtDlpManifest_DeclaresItsTranscodeTargets()
    {
        Assert.That(YtDlpDownloader.Caps.Containers,
            Is.EqualTo(new[] { AudioFormat.Mp3, AudioFormat.Opus, AudioFormat.Aac }));
        AssertLossy(YtDlpDownloader.Caps, AudioFormat.Mp3, 64, 320);
        Assert.That(YtDlpDownloader.Caps.SupportsLossless, Is.False,
            "YouTube has no lossless sources");
        Assert.That(YtDlpDownloader.Caps.SupportsLossy, Is.True);
    }

    [Test]
    public void SoundCloudManifest_DeclaresItsFloor()
    {
        Assert.That(SoundCloudDownloader.Caps.Containers, Is.EqualTo(new[] { AudioFormat.Mp3 }));
        AssertLossy(SoundCloudDownloader.Caps, AudioFormat.Mp3, 128, 320);
        Assert.That(SoundCloudDownloader.Caps.SupportsLossless, Is.False);
        Assert.That(SoundCloudDownloader.Caps.SupportsLossy, Is.True);
    }

    [Test]
    public void ArchiveOrgManifest_DeclaresItsFloor()
    {
        Assert.That(ArchiveOrgDownloader.Caps.Containers, Is.EqualTo(new[] { AudioFormat.Mp3 }));
        AssertLossy(ArchiveOrgDownloader.Caps, AudioFormat.Mp3, 128, 320);
        Assert.That(ArchiveOrgDownloader.Caps.SupportsLossless, Is.False,
            "archive.org hands back lossy MP3 uploads");
    }

    [Test]
    public void MonochromeManifest_DeclaresItsLosslessTier()
    {
        Assert.That(MonochromeDownloader.Caps.Containers,
            Is.EqualTo(new[] { AudioFormat.Flac }));
        Assert.That(MonochromeDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(MonochromeDownloader.Caps.SupportsLossy, Is.False,
            "the official API streams lossless only");
        Assert.That(MonochromeDownloader.Caps.Deliverables.All(d => d.Controlled), Is.True,
            "asking for FLAC gets FLAC: the one deliverable is controllable");
    }

    [Test]
    public void DabMusicManifest_DeclaresItsTiers()
    {
        Assert.That(DabMusicDownloader.Caps.Containers,
            Is.EqualTo(new[] { AudioFormat.Mp3, AudioFormat.Flac }));
        Assert.That(DabMusicDownloader.Caps.SupportsLossless, Is.True);
        AssertLossy(DabMusicDownloader.Caps, AudioFormat.Mp3, 320, 320);
    }

    [Test]
    public void LucidaManifest_IsHonestAboutBeingUncontrollable()
    {
        Assert.That(LucidaDownloader.Caps.Containers,
            Is.EquivalentTo(new[] { AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.Aac }));
        Assert.That(LucidaDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(LucidaDownloader.Caps.Deliverables.All(d => !d.Controlled), Is.True,
            "the API has no quality parameter, so nothing here is controllable");
    }

    [Test]
    public void DoubleDoubleManifest_IsHonestAboutBeingUncontrollable()
    {
        Assert.That(DoubleDoubleDownloader.Caps.Containers,
            Is.EquivalentTo(new[] { AudioFormat.Flac, AudioFormat.Mp3 }));
        Assert.That(DoubleDoubleDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(DoubleDoubleDownloader.Caps.Deliverables.All(d => !d.Controlled), Is.True,
            "the submit API has no quality parameter either");
    }

    [Test]
    public void Manifests_AreServedByTheirCapabilities()
    {
        // The manifests are routing inputs, not decoration: spot-check each
        // against a request it must accept and one it must reject.
        Assert.That(YtDlpDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Opus)), Is.True);
        Assert.That(YtDlpDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.False,
            "yt-dlp transcode targets are lossy content, never a lossless source");
        Assert.That(SoundCloudDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Mp3, MaxKbps: 96)), Is.False,
            "SoundCloud's 128 kbps floor cannot fit under a 96 cap");
        Assert.That(MonochromeDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.True);
        Assert.That(MonochromeDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Aac, MinKbps: 320, MaxKbps: 320)), Is.False,
            "the API streams lossless only: there is no lossy tier to serve");
        Assert.That(LucidaDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Opus)), Is.False);
        Assert.That(LucidaDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.True,
            "uncontrollable does not mean unusable: Lucida may hand back a FLAC");
        Assert.That(DoubleDoubleDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.True);
        Assert.That(ArchiveOrgDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Mp3, MaxKbps: 96)), Is.False,
            "the 128 kbps floor is real, not a comment");
        Assert.That(DabMusicDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Mp3, MaxKbps: 256)), Is.False,
            "DAB serves MP3 320 only, so a 256 cap cannot be honoured");
    }

    // ── Manager-level: routing is visible in the call order ──

    [Test]
    public async Task FlacQuality_DownloadsViaLosslessSpyFirst()
    {
        var calls = new List<string>();
        var lossy = new OrderingSpyDownloader("lossy", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320), calls);
        var lossless = new OrderingSpyDownloader("lossless", PluginCapabilities.LosslessSources(), calls, fileExt: "flac");
        var manager = new DownloadManager(
            new ListDownloaderRegistry(lossy, lossless),
            NullLogger<DownloadManager>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-route-{Guid.NewGuid():N}");

        try
        {
            var path = await manager.DownloadTrackAsync(new TrackQuery("A", "T", new DownloadQuality(AudioFormat.Flac)), dir, "mel-flac");

            Assert.That(path, Does.EndWith("lossless.flac"));
            Assert.That(calls, Is.EqualTo(new[] { "lossless", "lossless:download" }),
                "a strict FLAC request only ever runs the lossless source");
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public async Task IncompatibleOnlyRegistry_SpyNeverSearched_LastFailureNamesFilters()
    {
        var calls = new List<string>();
        var spy = new OrderingSpyDownloader("mp3-only", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320), calls);
        var manager = new DownloadManager(
            new ListDownloaderRegistry(spy),
            NullLogger<DownloadManager>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-route-{Guid.NewGuid():N}");

        try
        {
            var path = await manager.DownloadTrackAsync(new TrackQuery("A", "T", new DownloadQuality(AudioFormat.Flac)), dir, "mel-x");

            Assert.That(path, Is.Null);
            Assert.That(calls, Is.Empty, "no incompatible plugin runs at all");
            Assert.That(manager.LastFailure("mel-x"),
                Does.Contain("your quality filters excluded every plugin"));
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }
}
