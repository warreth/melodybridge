using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live DAB Music tests (dabmusic.xyz). The community service is
/// regularly offline or moved between domains, so the tests skip
/// themselves when it does not answer; when it is up, the real search
/// and stream endpoints are exercised end to end.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("DabMusic")]
public class DabMusicLiveTests
{
    private DabMusicDownloader _downloader = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _downloader = new DabMusicDownloader(http, NullLogger<DabMusicDownloader>.Instance);
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-dab-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task WhenUp_SearchFindsTheTrack_AndTheMp3TierDownloads()
    {
        if (!await _downloader.IsAvailableAsync())
            Assert.Ignore("dabmusic.xyz is down (community service); skipping the live check");

        var hit = await _downloader.SearchAsync(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
            expectedDuration: TimeSpan.FromSeconds(213));

        Assert.That(hit, Is.Not.Null, "the search must find this well-known track when the service is up");
        Assert.That(hit!.Title, Does.Contain("Never Gonna Give You Up"));

        // The MP3 320 tier keeps the live download light (~8 MB); a
        // 24/192 FLAC would be ~100 MB.
        var result = await _downloader.DownloadAsync(
            hit.SourceUrl!, _outDir, "mb-dab-live-1",
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 320));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(File.Exists(result.FilePath), Is.True);

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
        Assert.That(double.TryParse(duration.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds), Is.True);
        Assert.That(seconds, Is.InRange(190, 235), "the official 3:33 track, nothing shorter or longer");
    }
}
