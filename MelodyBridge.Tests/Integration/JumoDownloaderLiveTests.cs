using System.Diagnostics;
using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Jumo;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live Jumo tests: real search against the guarded API, real downloads
/// from the signed Qobuz CDN, real decryption and real file validation
/// with ffprobe. The service gates its API by network reputation, so the
/// tests skip (never fail) when this machine's requests are refused.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("Jumo")]
public class JumoDownloaderLiveTests
{
    private JumoDownloader _downloader = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var config = new StubPluginConfig();
        // CD quality keeps the live download light while still exercising
        // the lossless path (one segment table, native FLAC output).
        config.Set("jumo", JumoDownloader.FlacQualityKey, "6");
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _downloader = new JumoDownloader(
            new JumoClient(http, NullLogger<JumoClient>.Instance),
            config, NullLogger<JumoDownloader>.Instance);
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-jumo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<DownloaderSearchHit> SearchOrSkipAsync()
    {
        var hit = await _downloader.SearchAsync(
            new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any, TimeSpan.FromSeconds(320)));
        if (hit is null)
            Assert.Ignore("the jumo service refused the search from this network (bot gate or outage)");
        return hit!;
    }

    [Test]
    public async Task Search_FindsTheKnownTrack()
    {
        var hit = await SearchOrSkipAsync();

        Assert.That(hit.Title, Does.Contain("One More Time"));
        Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
        Assert.That(hit.SourceUrl, Does.StartWith("jumo:"));
        Assert.That(hit.Duration, Is.Not.Null);
    }

    [Test]
    public async Task Download_Mp3Tier_ProducesAReal320File()
    {
        var hit = await SearchOrSkipAsync();
        var result = await _downloader.DownloadAsync(
            hit.SourceUrl!, _outDir, "mb-jumo-live-mp3",
            new DownloadQuality(AudioFormat.Mp3, 320, 320));

        if (!result.Success && result.ErrorMessage?.Contains("preview") == true)
            Assert.Ignore("only a preview stream is available for this track right now");
        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(File.Exists(result.FilePath), Is.True);

        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".mp3"),
            "the bytes, not the requested quality, decide the container");

        var measured = BitrateProbe.MeasureKbps(result.FilePath!);
        Assert.That(measured, Is.Not.Null, "ffprobe must measure the file");
        Assert.That(measured!.Value, Is.InRange(310, 330), "the 320 tier must deliver ~320 kbps");

        AssertDuration(result.FilePath!, min: 180, max: 400);
    }

    [Test]
    public async Task Download_FlacTier_ProducesNativeLosslessFlac()
    {
        var hit = await SearchOrSkipAsync();
        var result = await _downloader.DownloadAsync(
            hit.SourceUrl!, _outDir, "mb-jumo-live-flac",
            new DownloadQuality(AudioFormat.Flac));

        if (!result.Success && result.ErrorMessage?.Contains("preview") == true)
            Assert.Ignore("only a preview stream is available for this track right now");
        if (!result.Success && result.ErrorMessage?.Contains("429") == true)
            Assert.Ignore("the service throttled this test run; the client retried and backed off as far as it should");
        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(File.Exists(result.FilePath), Is.True);

        var sniffed = AudioSignature.DetectContainer(result.FilePath!);
        Assert.That(sniffed, Is.EqualTo(".flac"),
            "a decrypted lossless stream must be a native FLAC, not a container wrapper");

        var measured = BitrateProbe.MeasureKbps(result.FilePath!);
        Assert.That(measured, Is.Not.Null, "ffprobe must measure the file");
        Assert.That(measured!.Value, Is.GreaterThan(500), "lossless FLAC is far above lossy bitrates");

        // The MELODY_ID tag must survive inside the real bytes (the
        // manager writes it, so this test applies that same step first).
        TaglibHelper.WriteMelodyId(result.FilePath!, "mb-jumo-live-flac");
        Assert.That(TaglibHelper.ReadMelodyId(result.FilePath!), Is.EqualTo("mb-jumo-live-flac"));

        AssertDuration(result.FilePath!, min: 180, max: 400);
    }

    /// <summary>ffprobe reads the real duration; a broken assembly cannot pass this.</summary>
    private static void AssertDuration(string path, int min, int max)
    {
        using var ffprobe = Process.Start(new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v quiet -show_entries format=duration -of csv=p=0 \"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        var duration = ffprobe!.StandardOutput.ReadToEnd();
        ffprobe.WaitForExit();
        Assert.That(ffprobe.ExitCode, Is.EqualTo(0), "ffprobe must accept the file");
        Assert.That(double.TryParse(duration.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds),
            Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.InRange(min, max), "One More Time runs a little over five minutes");
    }
}
