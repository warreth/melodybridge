using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live Monochrome tests against the official API: real search, a real
/// FLAC stream and real file validation with ffprobe. Requires network
/// access to tracks.monochrome.st (the live CI job runs these).
/// </summary>
[TestFixture]
[Category("Live")]
public class MonochromeLiveTests
{
    private MonochromeDownloader _downloader = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _downloader = new MonochromeDownloader(
            new HttpClient { BaseAddress = new Uri(MonochromeDownloader.DefaultBaseUrl) },
            new EmptyPluginConfig(),
            NullLogger<MonochromeDownloader>.Instance);
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-monochrome-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class EmptyPluginConfig : IPluginConfig
    {
        public Task<string> GetAsync(string pluginId, string key, CancellationToken ct = default)
            => Task.FromResult("");

        public Task SetAsync(string pluginId, string key, string? value, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_OfficialApiAnswers()
    {
        Assert.That(await _downloader.IsAvailableAsync(), Is.True,
            "tracks.monochrome.st must answer /health for this test");
    }

    [Test]
    public async Task Search_WellKnownTrack_FindsItWithHighConfidence()
    {
        var hit = await _downloader.SearchAsync(
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null, "the official API must find this well-known track");
        Assert.That(hit!.Title, Does.Contain("Never Gonna Give You Up"));
        Assert.That(hit.Artist, Does.Contain("Rick Astley"));
        Assert.That(hit.Duration, Is.Not.Null);
        Assert.That(hit.Duration!.Value.TotalSeconds, Is.InRange(195, 235), "the album version runs ~3:33");
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(hit.SourceUrl, Does.Contain("/track/"));
    }

    [Test]
    public async Task Download_RealTrack_ProducesAValidFlac()
    {
        var hit = await _downloader.SearchAsync(
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));
        Assert.That(hit, Is.Not.Null, "precondition: search finds the track");

        var result = await _downloader.DownloadAsync(hit!.SourceUrl!, _outDir, "mb-monochrome-live");
        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(File.Exists(result.FilePath), Is.True, "reported file must exist");

        // The bytes must be a real FLAC, not an error page: ffprobe has the last word.
        var codec = await FfprobeAsync("-v error -show_entries stream=codec_name -of csv=p=0", result.FilePath!);
        var codecs = codec.Split(new[] { ',', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(codecs, Is.Not.Empty, "ffprobe must see at least one stream");
        Assert.That(codecs, Is.All.EqualTo("flac"),
            "the official API streams lossless FLAC: one audio stream, no video");

        // Duration sanity: the file really is the ~3:33 track.
        var duration = await FfprobeAsync("-v quiet -show_entries format=duration -of csv=p=0", result.FilePath!);
        Assert.That(double.TryParse(duration.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds),
            Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.InRange(195, 235));

        Assert.That(new FileInfo(result.FilePath!).Length, Is.GreaterThan(5 * 1024 * 1024),
            "a full-length FLAC is megabytes, not a stub");
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
