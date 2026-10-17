using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The deemix plugin against the real world: the public Deezer search
/// always runs, and the CLI download runs when DEEMIX_COMMAND and
/// DEEMIX_DOWNLOADS_PATH point at a logged-in deemix (the same setup the
/// plugin settings describe). Without those the download test skips.
/// </summary>
[TestFixture]
[Category("Live")]
public class DeemixLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-deemix-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static DeemixDownloader Create(StubPluginConfig config)
        => new(new DeezerClient(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                NullLogger<DeezerClient>.Instance),
            config, NullLogger<DeemixDownloader>.Instance);

    [Test]
    public async Task Search_PublicApi_FindsWellKnownTracks()
    {
        var plugin = Create(new StubPluginConfig());

        var hit = await plugin.SearchAsync(new TrackQuery(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(213)));

        Assert.That(hit, Is.Not.Null, "the public Deezer search must find this well-known track");
        Assert.Multiple(() =>
        {
            Assert.That(hit!.Title, Does.Contain("Never Gonna Give You Up"));
            Assert.That(hit.SourceUrl, Does.StartWith("https://www.deezer.com/track/"));
            Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        });
    }

    [Test]
    public async Task Download_ThroughTheDeemixCli_ProducesValidAudio()
    {
        var command = Environment.GetEnvironmentVariable("DEEMIX_COMMAND");
        var downloads = Environment.GetEnvironmentVariable("DEEMIX_DOWNLOADS_PATH");
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(downloads))
            Assert.Ignore("DEEMIX_COMMAND and DEEMIX_DOWNLOADS_PATH must point at a logged-in deemix");

        var config = new StubPluginConfig();
        config.Set("deemix", "command", command);
        config.Set("deemix", "downloads_path", downloads);
        var plugin = Create(config);

        Assert.That(await plugin.IsAvailableAsync(), Is.True,
            "the deemix command must be resolvable");

        var hit = await plugin.SearchAsync(new TrackQuery(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(213)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds a source");

        var result = await plugin.DownloadAsync(hit!.SourceUrl!, _outDir, "mb-deemix-live",
            new DownloadQuality(AudioFormat.Mp3, MinKbps: 320, MaxKbps: 320));

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(File.Exists(result.FilePath), Is.True, "reported file must exist");
        Assert.That(new FileInfo(result.FilePath!).Length, Is.GreaterThan(1024 * 1024),
            "a full track is megabytes, not a stub");

        // The audio stream only: the embedded cover art shows up as an
        // extra mjpeg stream on the same file, and ffprobe's csv writer
        // adds a trailing comma for some streams.
        var codec = await FfprobeAsync(
            "-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0",
            result.FilePath!);
        var codecs = codec.Split(new[] { ',', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(codecs, Is.EqualTo(new[] { "mp3" }), "the 320 tier arrives as MP3");

        var duration = await FfprobeAsync("-v quiet -show_entries format=duration -of csv=p=0",
            result.FilePath!);
        Assert.That(double.TryParse(duration.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds), Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.InRange(195, 235), "the 3:33 studio recording");
    }

    [Test]
    public async Task Download_WithoutAValidLogin_FailsHonestly()
    {
        var cli = Environment.GetEnvironmentVariable("DEEMIX_CLI_JS");
        if (string.IsNullOrWhiteSpace(cli))
            Assert.Ignore("DEEMIX_CLI_JS must point at the built deemix CLI (packages/cli/dist/main.cjs)");
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell wrapper");

        // A throwaway portable config with a bogus ARL: the real CLI runs,
        // refuses to log in, and the plugin must report that honestly.
        var home = Path.Combine(_outDir, $"nologin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "config"));
        File.WriteAllText(Path.Combine(home, "config", "login.json"),
            "{\"arl\":\"mb-invalid-arl\"}");
        var downloads = Path.Combine(_outDir, $"downloads-{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloads);

        var config = new StubPluginConfig();
        config.Set("deemix", "command",
            $"sh -c 'cd {home} && node {cli} --portable \"$@\"' deemix");
        config.Set("deemix", "downloads_path", downloads);
        var plugin = Create(config);

        var result = await plugin.DownloadAsync(
            "https://www.deezer.com/track/3135556", _outDir, "mb-deemix-nologin",
            new DownloadQuality(AudioFormat.Mp3, MinKbps: 128, MaxKbps: 128));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "the failure must say what the CLI reported");
            Assert.That(Directory.GetDirectories(downloads), Is.Empty,
                "no per-track folder may survive a failed run");
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
