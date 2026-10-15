using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The non-strict fallback ladder: every step comes from the enabled
/// plugins' manifests, the cap of the request is never exceeded, and the
/// container only changes when the playlist allows it. Strict playlists
/// never build a ladder at all: they get exactly the requested quality.
/// </summary>
[TestFixture]
public class QualityLadderTests
{
    /// <summary>Manifest carrier only; the ladder never searches.</summary>
    private sealed class Stub(string id, PluginCapabilities caps) : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public PluginCapabilities Capabilities => caps;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
    }

    private static readonly IDownloader[] LosslessAndLossy =
    [
        new Stub("lossless", PluginCapabilities.LosslessSources()),
        new Stub("lossy", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320)),
    ];

    [Test]
    public void LosslessRequest_FallsToTheBestDeclaredLossyLevel()
    {
        var targets = QualityLadder.Build(
            new DownloadQuality(AudioFormat.Flac), allowContainerChange: true, LosslessAndLossy);

        Assert.That(targets, Is.EqualTo(new[]
        {
            new DownloadQuality(AudioFormat.Flac),
            new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 320),
        }), "the request first, then the best level the lossy container declares");
    }

    [Test]
    public void LosslessRequest_ContainerChangeOff_KeepsOnlyTheRequest()
    {
        var targets = QualityLadder.Build(
            new DownloadQuality(AudioFormat.Flac), allowContainerChange: false, LosslessAndLossy);

        Assert.That(targets, Is.EqualTo(new[] { new DownloadQuality(AudioFormat.Flac) }),
            "a FLAC request cannot step down when containers must stay");
    }

    [Test]
    public void FlooredRequest_DropsTheFloorBeforeAnythingElse()
    {
        var targets = QualityLadder.Build(
            new DownloadQuality(AudioFormat.Mp3, MinKbps: 192, MaxKbps: 320),
            allowContainerChange: false,
            [new Stub("mp3", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320))]);

        Assert.That(targets, Is.EqualTo(new[]
        {
            new DownloadQuality(AudioFormat.Mp3, MinKbps: 192, MaxKbps: 320),
            new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 320),
        }), "the highest file below the cap is exactly one step down");
    }

    [Test]
    public void TieredPlugins_ContributeTheirOwnLevels()
    {
        IDownloader[] plugins =
        [
            new Stub("dab", new PluginCapabilities(
            [
                new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 320, 320),
                new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
            ])),
            new Stub("monochrome", new PluginCapabilities(
            [
                new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
                new QualityDeliverable(AudioFormat.Aac, Lossless: false, 96, 96),
                new QualityDeliverable(AudioFormat.Aac, Lossless: false, 320, 320),
            ])),
        ];

        var targets = QualityLadder.Build(new DownloadQuality(AudioFormat.Flac), true, plugins);

        Assert.That(targets.Select(t => t.Format),
            Is.EqualTo(new[] { AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.Aac }),
            "each declared container adds exactly one step, best anchor first");
        Assert.That(targets.Skip(1).Select(t => t.MaxKbps), Is.EqualTo(new int?[] { 320, 320 }));
    }

    [Test]
    public void NothingServable_ReturnsEmpty_ForTheManagerToReportTheRequest()
    {
        var targets = QualityLadder.Build(
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 96),
            allowContainerChange: true,
            [new Stub("mp3-320", PluginCapabilities.Lossy(AudioFormat.Mp3, 320, 320))]);

        Assert.That(targets, Is.Empty, "this plugin cannot serve anything under 96 kbps");
    }

    [Test]
    public void Ladder_NeverExceedsTheRequestedCap_AndNeverGoesLossless()
    {
        var targets = QualityLadder.Build(
            new DownloadQuality(AudioFormat.Mp3, MinKbps: 128, MaxKbps: 320),
            allowContainerChange: true, LosslessAndLossy);

        Assert.That(targets.All(t => t.MaxKbps is null or <= 320), Is.True,
            "stepping down never asks for more than the playlist's cap");
        Assert.That(targets.All(t => t.Format != AudioFormat.Flac), Is.True,
            "a lossy request never steps up to lossless");
    }

    // ── The manager walks the ladder only when the playlist is not strict ──

    /// <summary>Spy that answers a hit and writes a file, like a plugin would.</summary>
    private sealed class LadderSpy(
        string id, PluginCapabilities caps, List<string> calls, string ext = "mp3", bool downloadFails = false) : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public PluginCapabilities Capabilities => caps;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            calls.Add(id);
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, $"https://{id}.example/1", TimeSpan.FromSeconds(1)));
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            calls.Add(id + ":download");
            if (downloadFails)
                return Task.FromResult(new DownloaderDownloadResult(false, null, "stub refuses"));

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{id}.{ext}");
            File.WriteAllBytes(path, ext == "flac" ? "fLaC"u8.ToArray() : TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private static DownloadManager Manager(params IDownloader[] plugins)
        => new(new ListDownloaderRegistry(plugins), NullLogger<DownloadManager>.Instance);

    [Test]
    public async Task NonStrict_StepsDownWhenTheLosslessSourceFails()
    {
        var calls = new List<string>();
        var lossless = new LadderSpy("failing-lossless",
            PluginCapabilities.LosslessSources(), calls, "flac", downloadFails: true);
        var lossy = new LadderSpy("lossy", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320), calls);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-ladder-{Guid.NewGuid():N}");

        try
        {
            var path = await Manager(lossless, lossy).DownloadTrackAsync(
                new TrackQuery("A", "T", new DownloadQuality(AudioFormat.Flac)), dir, "mel-ladder",
                new DownloadFallback(AllowContainerChange: true));

            Assert.That(path, Is.Not.Null.And.EndsWith("lossy.mp3"),
                "the ladder lands on the best lossy file");
            Assert.That(calls, Is.EqualTo(new[] { "failing-lossless", "failing-lossless:download", "lossy", "lossy:download" }));
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public async Task Strict_DoesNotStepDown_AndSaysSo()
    {
        var calls = new List<string>();
        var lossless = new LadderSpy("failing-lossless",
            PluginCapabilities.LosslessSources(), calls, "flac", downloadFails: true);
        var lossy = new LadderSpy("lossy", PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320), calls);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-ladder-{Guid.NewGuid():N}");

        try
        {
            var manager = Manager(lossless, lossy);
            var path = await manager.DownloadTrackAsync(
                new TrackQuery("A", "T", new DownloadQuality(AudioFormat.Flac)), dir, "mel-strict");

            Assert.That(path, Is.Null, "strict never leaves the requested quality");
            Assert.That(calls, Is.EqualTo(new[] { "failing-lossless", "failing-lossless:download" }),
                "the lossy plugin is excluded from a FLAC request and never tried");
            Assert.That(manager.LastFailure("mel-strict"), Is.Not.Null.And.Not.Empty,
                "the failure carries the real reason");
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }
}
