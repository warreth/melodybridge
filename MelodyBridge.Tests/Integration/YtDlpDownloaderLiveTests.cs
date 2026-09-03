using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live yt-dlp tests: real YouTube search, real download, real file validation.
/// Requires the yt-dlp binary on PATH (CI installs it in the live job).
/// These cannot be cheated: every assertion reads the produced file.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("YtDlp")]
public class YtDlpDownloaderLiveTests
{
    private YtDlpDownloader _downloader = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _downloader = new YtDlpDownloader(NullLogger<YtDlpDownloader>.Instance);
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-ytdlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task IsAvailable_FindsBinaryOnPath()
    {
        var available = await _downloader.IsAvailableAsync();
        Assert.That(available, Is.True, "yt-dlp must be on PATH for this test");
    }

    [Test]
    public async Task Search_ClassicalTrack_FindsYouTubeUrl()
    {
        var hit = await _downloader.SearchAsync("Ludwig van Beethoven", "Für Elise", MelodyBridge.Core.DownloadQuality.Any);
        Assert.That(hit, Is.Not.Null, "ytsearch must find this well-known track");
        Assert.That(hit!.SourceUrl, Does.StartWith("https://www.youtube.com/watch?v="));
        Assert.That(hit.Title, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Download_InvalidUrl_ReturnsFailureNotThrow()
    {
        var result = await _downloader.DownloadAsync(
            "https://www.youtube.com/watch?v=definitely-not-a-real-id-xyz",
            _outDir, "mb-test-invalid");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
        Assert.That(result.FilePath, Is.Null, "failed downloads must not report a file");
    }

    [Test]
    public async Task Download_KnownTrack_ProducesValidTaggedAudioFile()
    {
        // Search first, then download the hit.
        var hit = await _downloader.SearchAsync("Ludwig van Beethoven", "Für Elise", MelodyBridge.Core.DownloadQuality.Any);
        Assert.That(hit, Is.Not.Null, "precondition: search finds the track");

        var result = await _downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-test-melody-123");

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(System.IO.File.Exists(result.FilePath), Is.True, "reported file must exist");

        // The MELODY_ID tag must be readable from the actual file.
        var melodyId = MelodyBridge.Infrastructure.Tagging.TaglibHelper.ReadMelodyId(result.FilePath!);
        Assert.That(melodyId, Is.EqualTo("mb-test-melody-123"),
            "downloaded file must carry the MELODY_ID tag");

        // The file must be real audio with a sensible duration.
        using var ffprobe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v quiet -show_entries format=duration -of csv=p=0 \"{result.FilePath}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        var duration = await ffprobe!.StandardOutput.ReadToEndAsync();
        await ffprobe.WaitForExitAsync();
        Assert.That(ffprobe.ExitCode, Is.EqualTo(0), "ffprobe must accept the file");
        Assert.That(double.TryParse(duration.Trim(), out var seconds), Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.GreaterThan(60).And.LessThan(600), "Für Elise is ~3-4 minutes");
    }
}
