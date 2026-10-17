using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Qobuz against the real service and a real qobuz-dlp. The token comes
/// from QOBUZ_TOKEN or from qobuz-dlp's own token file; the download test
/// also needs QOBUZ_DLP_COMMAND (or qobuz-dlp on PATH). Without those the
/// tests skip, like the media-server live fixtures.
/// </summary>
[TestFixture]
[Category("Live")]
public class QobuzLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-qobuz-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static string? Token()
        => Environment.GetEnvironmentVariable("QOBUZ_TOKEN") ?? QobuzDownloader.ReadQobuzDlpTokenFile();

    private static QobuzClient Client()
        => new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<QobuzClient>.Instance);

    [Test]
    public async Task Search_WithAToken_FindsWellKnownTracks()
    {
        var token = Token();
        if (string.IsNullOrWhiteSpace(token))
            Assert.Ignore("QOBUZ_TOKEN or qobuz-dlp's token file is required");

        var tracks = await Client().SearchTracksAsync(
            "Daft Punk Around the World", 5, token);

        Assert.That(tracks, Is.Not.Empty, "the live Qobuz search must find this well-known track");
        var best = QobuzDownloader.Rank(tracks,
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any,
                ExpectedDuration: TimeSpan.FromSeconds(429)));
        Assert.That(best, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(best!.Title, Does.Contain("Around the World"));
            Assert.That(best.Artist, Does.Contain("Daft Punk"));
            Assert.That(best.SourceUrl, Does.StartWith("https://play.qobuz.com/track/"));
        });
    }

    [Test]
    public async Task Download_ThroughQobuzDlp_ProducesValidAudio()
    {
        var token = Token();
        var command = Environment.GetEnvironmentVariable("QOBUZ_DLP_COMMAND");
        if (string.IsNullOrWhiteSpace(token))
            Assert.Ignore("QOBUZ_TOKEN or qobuz-dlp's token file is required");
        if (string.IsNullOrWhiteSpace(command))
            Assert.Ignore("QOBUZ_DLP_COMMAND must point at a logged-in qobuz-dlp");

        var config = new StubPluginConfig();
        config.Set("qobuz", "command", command);
        config.Set("qobuz", "user_auth_token", token);
        var plugin = new QobuzDownloader(Client(), config, NullLogger<QobuzDownloader>.Instance);

        var check = await Client().ValidateTokenAsync(token);
        if (check.Account is { CanDownload: false })
            Assert.Ignore("the Qobuz account cannot download (a paid plan is required)");

        Assert.That(await plugin.IsAvailableAsync(), Is.True,
            "the token and the qobuz-dlp command must both be valid");

        var hit = await plugin.SearchAsync(new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds a source");

        var result = await plugin.DownloadAsync(hit!.SourceUrl!, _outDir, "mb-qobuz-live",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(File.Exists(result.FilePath), Is.True, "reported file must exist");
        Assert.That(new FileInfo(result.FilePath!).Length, Is.GreaterThan(1024 * 1024),
            "a lossless track is megabytes, not a stub");

        // The audio stream only: the embedded cover art shows up as an
        // extra mjpeg stream on the same file, and ffprobe's csv writer
        // adds a trailing comma for some streams.
        var codec = await FfprobeAsync(
            "-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0",
            result.FilePath!);
        var codecs = codec.Split(new[] { ',', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(codecs, Is.EqualTo(new[] { "flac" }), "qobuz-dlp must deliver lossless audio");

        var duration = await FfprobeAsync("-v quiet -show_entries format=duration -of csv=p=0",
            result.FilePath!);
        Assert.That(double.TryParse(duration.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds), Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.InRange(380, 480), "the ~7 minute album version");
    }

    [Test]
    public async Task Download_WithAFreePlan_FailsHonestly()
    {
        var token = Token();
        var command = Environment.GetEnvironmentVariable("QOBUZ_DLP_COMMAND");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(command))
            Assert.Ignore("QOBUZ_TOKEN (or the token file) and QOBUZ_DLP_COMMAND are required");

        var check = await Client().ValidateTokenAsync(token);
        if (check.Account is null) Assert.Ignore("the Qobuz token is stale");
        if (check.Account.CanDownload)
            Assert.Ignore("a paid plan can download: covered by the download test");

        var config = new StubPluginConfig();
        config.Set("qobuz", "command", command);
        config.Set("qobuz", "user_auth_token", token);
        var plugin = new QobuzDownloader(Client(), config, NullLogger<QobuzDownloader>.Instance);

        Assert.That(await plugin.IsAvailableAsync(), Is.False,
            "a plan that cannot download must keep the plugin out of the waterfall");

        // And when the waterfall would still call it, the real CLI runs and
        // the plugin reports the honest failure, never a fake success.
        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/1065482", _outDir, "mb-qobuz-free");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "the failure must say what happened");
            Assert.That(Directory.GetFiles(_outDir), Is.Empty, "no file may reach the playlist folder");
        });
    }

    private static async Task<string> FfprobeAsync(string arguments, string file)
    {
        using var ffprobe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"{arguments} \"{file}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = await ffprobe.StandardOutput.ReadToEndAsync();
        await ffprobe.WaitForExitAsync();
        return stdout;
    }
}
