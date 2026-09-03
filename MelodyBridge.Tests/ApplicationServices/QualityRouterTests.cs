using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Lucida;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The quality router: hard capability gate before any plugin runs, then
/// ranking that prefers small-file specialists under a cap and lossless
/// sources for FLAC. Excluded plugins are never searched — the spies
/// prove it by counting calls.
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

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
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
        string fileExt = "mp3") : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public PluginCapabilities Capabilities => caps;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
        {
            callOrder.Add(id);
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(title, artist, $"https://{id}.example/1", TimeSpan.FromSeconds(1)));
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            callOrder.Add(id + ":download");
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{id}.{fileExt}");
            File.WriteAllText(path, "x");
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private static PluginCapabilities Mp3(int? min = null, int? max = null, bool lossless = false, bool lossy = true)
        => new([AudioFormat.Mp3], min, max, lossless, lossy);

    /// <summary>A FLAC-only source: honest bitrate floors start where lossless actually lives.</summary>
    private static PluginCapabilities FlacOnly(int? min = 800, int? max = null, bool lossy = false)
        => new([AudioFormat.Flac], min, max, true, lossy);

    // ── 1. Space Saver (cap 160): small-file plugin first, FLAC-only plugin excluded ──

    [Test]
    public void SpaceSaver_SmallFilePluginRanksFirst_LosslessOnlyExcluded()
    {
        var small = new CapabilityDownloader("small", Mp3(min: 64, max: 320));
        var flacOnly = new CapabilityDownloader("flac-only", FlacOnly());
        var plan = QualityRouter.Route(
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160),
            [small, flacOnly]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(), Is.EqualTo(new[] { "small" }),
            "the small-file specialist is the only survivor under a 160 cap");
        Assert.That(plan.Plugins[0].Id, Is.EqualTo("small"), "small-file specialist ranks first");
        var (plugin, reason) = plan.Excluded.Single();
        Assert.That(plugin.Id, Is.EqualTo("flac-only"));
        Assert.That(reason, Does.Contain("160"),
            "the reason names the cap the plugin cannot serve under");
    }

    [Test]
    public async Task SpaceSaver_LosslessOnlyPlugin_IsNeverSearched()
    {
        var spy = new List<string>();
        var flacOnly = new CapabilityDownloader("flac-only", FlacOnly(), spy);
        var manager = new DownloadManager(
            new ListDownloaderRegistry(flacOnly),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("A", "T", Path.GetTempPath(), "mel-cap",
            quality: new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160));

        Assert.That(path, Is.Null);
        Assert.That(spy, Is.Empty, "an excluded plugin must never hit the network");
        Assert.That(manager.LastFailure("mel-cap"),
            Does.Contain("your quality filters excluded every plugin"),
            "the failure says the filters, not the sources, were the problem");
    }

    // ── 2. Lossless FLAC: lossless-capable ranks before lossy-only, both present ──

    [Test]
    public void LosslessFlac_LosslessCapableRanksBeforeLossyOnly()
    {
        // "lossy-only" here still carries the FLAC container (a FLAC
        // transcoder), so the format gate passes and only the ranking
        // decides — that is what the lossless-first rule is for.
        var lossy = new CapabilityDownloader("lossy",
            new PluginCapabilities([AudioFormat.Flac, AudioFormat.Mp3], null, null, false, true));
        var lossless = new CapabilityDownloader("lossless",
            new PluginCapabilities([AudioFormat.Flac, AudioFormat.Mp3], null, null, true, true));
        var plan = QualityRouter.Route(new DownloadQuality(AudioFormat.Flac), [lossy, lossless]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(),
            Is.EqualTo(new[] { "lossless", "lossy" }),
            "FLAC request: whoever can actually serve lossless runs first");
        Assert.That(plan.Excluded, Is.Empty, "the lossy plugin still runs as a fallback");
    }

    // ── 3. Strict filter (Opus, ceiling 160): mp3-only plugin excluded with reason ──

    [Test]
    public void StrictOpusCeiling160_Mp3OnlyPluginExcludedWithReason_NeverSearched()
    {
        var spy = new List<string>();
        var mp3Only = new CapabilityDownloader("mp3-only", Mp3(), spy);
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
        var fatOnly = new CapabilityDownloader("fat-only", Mp3(min: 128), spy);
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
                new CapabilityDownloader("a", Mp3(min: 256), spy),
                new CapabilityDownloader("b", FlacOnly(), spy)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("A", "T", Path.GetTempPath(), "mel-all",
            quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 96));

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
            new CapabilityDownloader("third", FlacOnly()),
            new CapabilityDownloader("first", Mp3(max: 96)),
            new CapabilityDownloader("second", Mp3(min: 256)),
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
            [new CapabilityDownloader("flac", FlacOnly())]);

        Assert.That(plan.Plugins.Select(p => p.Id).ToArray(), Is.EqualTo(new[] { "flac" }),
            "lossless band is meaningless: FLAC requests ignore MaxKbps");
        Assert.That(plan.Excluded, Is.Empty);
    }

    // ── 8. Manifests: the six real plugins expose the spec'd Caps ──

    [Test]
    public void YtDlpManifest_MatchesSpec()
    {
        Assert.That(YtDlpDownloader.Caps.Containers,
            Is.EqualTo(new[] { AudioFormat.Mp3, AudioFormat.Opus, AudioFormat.Aac }));
        Assert.That(YtDlpDownloader.Caps.MinKbps, Is.EqualTo(64));
        Assert.That(YtDlpDownloader.Caps.MaxKbps, Is.EqualTo(320));
        Assert.That(YtDlpDownloader.Caps.SupportsLossless, Is.False);
        Assert.That(YtDlpDownloader.Caps.SupportsLossy, Is.True);
    }

    [Test]
    public void SoundCloudManifest_MatchesSpec()
    {
        Assert.That(SoundCloudDownloader.Caps.Containers, Is.EqualTo(new[] { AudioFormat.Mp3 }));
        Assert.That(SoundCloudDownloader.Caps.MinKbps, Is.EqualTo(128));
        Assert.That(SoundCloudDownloader.Caps.MaxKbps, Is.EqualTo(320));
        Assert.That(SoundCloudDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(SoundCloudDownloader.Caps.SupportsLossy, Is.False);
    }

    [Test]
    public void MonochromeManifest_MatchesSpec()
    {
        Assert.That(MonochromeDownloader.Caps.Containers,
            Is.EqualTo(new[] { AudioFormat.Flac, AudioFormat.Aac }));
        Assert.That(MonochromeDownloader.Caps.MinKbps, Is.Null);
        Assert.That(MonochromeDownloader.Caps.MaxKbps, Is.Null);
        Assert.That(MonochromeDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(MonochromeDownloader.Caps.SupportsLossy, Is.True);
    }

    [Test]
    public void LucidaManifest_MatchesSpec()
    {
        Assert.That(LucidaDownloader.Caps.Containers, Is.EqualTo(
            new[] { AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.Aac }));
        Assert.That(LucidaDownloader.Caps.MinKbps, Is.Null);
        Assert.That(LucidaDownloader.Caps.MaxKbps, Is.Null);
        Assert.That(LucidaDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(LucidaDownloader.Caps.SupportsLossy, Is.True);
    }

    [Test]
    public void DoubleDoubleManifest_MatchesSpec()
    {
        Assert.That(DoubleDoubleDownloader.Caps.Containers, Is.EqualTo(
            new[] { AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.Aac }));
        Assert.That(DoubleDoubleDownloader.Caps.MinKbps, Is.Null);
        Assert.That(DoubleDoubleDownloader.Caps.MaxKbps, Is.Null);
        Assert.That(DoubleDoubleDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(DoubleDoubleDownloader.Caps.SupportsLossy, Is.True);
    }

    [Test]
    public void ArchiveOrgManifest_MatchesSpec()
    {
        Assert.That(ArchiveOrgDownloader.Caps.Containers, Is.EqualTo(new[] { AudioFormat.Mp3 }));
        Assert.That(ArchiveOrgDownloader.Caps.MinKbps, Is.Null);
        Assert.That(ArchiveOrgDownloader.Caps.MaxKbps, Is.EqualTo(320));
        Assert.That(ArchiveOrgDownloader.Caps.SupportsLossless, Is.True);
        Assert.That(ArchiveOrgDownloader.Caps.SupportsLossy, Is.False);
    }

    [Test]
    public void Manifests_AreServedByTheirCapabilities()
    {
        // The manifests are routing inputs, not decoration: spot-check each
        // against a request it must accept and one it must reject.
        Assert.That(YtDlpDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Opus)), Is.True);
        Assert.That(YtDlpDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.False);
        Assert.That(SoundCloudDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Mp3, MaxKbps: 96)), Is.False,
            "SoundCloud's 128 kbps floor cannot fit under a 96 cap");
        Assert.That(MonochromeDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.True);
        Assert.That(LucidaDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Opus)), Is.False);
        Assert.That(DoubleDoubleDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Flac)), Is.True);
        Assert.That(ArchiveOrgDownloader.Caps.CanServe(new DownloadQuality(AudioFormat.Mp3, MaxKbps: 96)), Is.True,
            "null plugin floor is unbounded: the cap alone excludes nothing");
    }

    // ── Manager-level: routing is visible in the call order ──

    [Test]
    public async Task FlacQuality_DownloadsViaLosslessSpyFirst()
    {
        var calls = new List<string>();
        var lossy = new OrderingSpyDownloader("lossy", Mp3(), calls);
        var lossless = new OrderingSpyDownloader("lossless",
            new PluginCapabilities([AudioFormat.Flac, AudioFormat.Mp3], null, null, true, true),
            calls, fileExt: "flac");
        var manager = new DownloadManager(
            new ListDownloaderRegistry(lossy, lossless),
            NullLogger<DownloadManager>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-route-{Guid.NewGuid():N}");

        try
        {
            var path = await manager.DownloadTrackAsync("A", "T", dir, "mel-flac",
                quality: new DownloadQuality(AudioFormat.Flac));

            Assert.That(path, Does.EndWith("lossless.flac"));
            Assert.That(calls, Is.EqualTo(new[] { "lossless", "lossless:download" }),
                "FLAC request: the lossless source is searched and used first");
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
        var spy = new OrderingSpyDownloader("mp3-only", Mp3(), calls);
        var manager = new DownloadManager(
            new ListDownloaderRegistry(spy),
            NullLogger<DownloadManager>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-route-{Guid.NewGuid():N}");

        try
        {
            var path = await manager.DownloadTrackAsync("A", "T", dir, "mel-x",
                quality: new DownloadQuality(AudioFormat.Flac));

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
