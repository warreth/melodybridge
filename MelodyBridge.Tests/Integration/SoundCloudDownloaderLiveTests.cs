using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live SoundCloud plugin tests via yt-dlp scsearch: real network,
/// real download, real file validation. Every assertion reads the
/// produced file: no mocks anywhere on this path.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("SoundCloud")]
public class SoundCloudDownloaderLiveTests
{
    private SoundCloudDownloader _downloader = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _downloader = new SoundCloudDownloader(NullLogger<SoundCloudDownloader>.Instance);
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-sc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Search_FindsSoundCloudUrl()
    {
        var hit = await _downloader.SearchAsync("Forss", "Flickermood", MelodyBridge.Core.DownloadQuality.Any);
        Assert.That(hit, Is.Not.Null, "scsearch must find this well-known SoundCloud track");
        Assert.That(hit!.SourceUrl, Does.Contain("soundcloud.com"));
        Assert.That(hit.Title, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Download_ProducesHighBitrateTaggedMp3()
    {
        var hit = await _downloader.SearchAsync("Forss", "Flickermood", MelodyBridge.Core.DownloadQuality.Any);
        Assert.That(hit, Is.Not.Null, "precondition: search finds the track");

        var result = await _downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-sc-test-1");

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(System.IO.File.Exists(result.FilePath), Is.True);

        // MELODY_ID must be in the actual file bytes.
        var tag = MelodyBridge.Infrastructure.Tagging.TaglibHelper.ReadMelodyId(result.FilePath!);
        Assert.That(tag, Is.EqualTo("mb-sc-test-1"));

        // Quality gate: the file itself must be at least 128 kbps: verify via ffprobe.
        var (ok, bitrate) = FfprobeBitrateKbps(result.FilePath!);
        Assert.That(ok, Is.True, "ffprobe must accept the file");
        Assert.That(bitrate, Is.GreaterThanOrEqualTo(128),
            $"downloaded file is only {bitrate} kbps: the quality gate would reject this");
    }

    [Test]
    public async Task Download_NonSoundCloudUrl_FailsGracefully()
    {
        var result = await _downloader.DownloadAsync(
            "https://example.com/not-a-track", _outDir, "mb-sc-test-2");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty);
    }

    /// <summary>Reads the real audio bitrate via ffprobe.</summary>
    private static (bool ok, int? bitrate) FfprobeBitrateKbps(string file)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v quiet -show_entries format=bit_rate -of csv=p=0 \"{file}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return (proc.ExitCode == 0 && int.TryParse(output.Trim(), out var bps)
                ? (true, bps / 1000)
                : (proc.ExitCode == 0, null));
        }
        catch { return (false, null); }
    }
}
