using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live waterfall test: the real DownloadManager over the real yt-dlp
/// plugin, real network, real file validation. Proves the full path a
/// playlist track takes: search (ranked), duration gate, download,
/// signature check, MELODY_ID tagging.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("YtDlp")]
public class DownloadManagerLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-mgr-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static DownloadManager Manager()
        => new(new ListDownloaderRegistry(new YtDlpDownloader(NullLogger<YtDlpDownloader>.Instance)),
            NullLogger<DownloadManager>.Instance);

    [Test]
    public async Task DownloadTrack_ThroughTheRealWaterfall_WritesTaggedAudio()
    {
        var manager = Manager();

        var path = await manager.DownloadTrackAsync(
            "Rick Astley", "Never Gonna Give You Up", _outDir, "mb-live-mgr-1",
            DownloadQuality.Any, expectedDuration: TimeSpan.FromSeconds(214));

        Assert.That(path, Is.Not.Null, manager.LastFailure("mb-live-mgr-1"));
        Assert.That(File.Exists(path), Is.True);

        // The manager's one tagging point: the id lives inside the bytes.
        Assert.That(TaglibHelper.ReadMelodyId(path!), Is.EqualTo("mb-live-mgr-1"));

        // And the file is the right track, not a snippet: ~3:33 of audio.
        using var ffprobe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v quiet -show_entries format=duration -of csv=p=0 \"{path}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        var duration = await ffprobe!.StandardOutput.ReadToEndAsync();
        await ffprobe.WaitForExitAsync();
        // Invariant culture: ffprobe prints a dot decimal; a Dutch locale
        // would read "213.58" as 213,580,000.
        Assert.That(double.TryParse(duration.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds), Is.True);
        Assert.That(seconds, Is.InRange(195, 235), "the official 3:33 track, nothing shorter or longer");

        var progress = manager.SnapshotProgress().Single(p => p.MelodyId == "mb-live-mgr-1");
        Assert.That(progress.Status, Is.EqualTo("done"));
        Assert.That(progress.MatchConfidence, Is.EqualTo(MatchConfidence.High));
    }

    [Test]
    public async Task DownloadTrack_WrongLengthExpectation_RejectsEveryCandidate()
    {
        var manager = Manager();

        // The playlist claims 100 seconds; every real source of this track
        // runs ~3:33. Downloading one anyway would be the wrong track.
        var path = await manager.DownloadTrackAsync(
            "Rick Astley", "Never Gonna Give You Up", _outDir, "mb-live-mgr-2",
            DownloadQuality.Any, expectedDuration: TimeSpan.FromSeconds(100));

        Assert.That(path, Is.Null, "a gross duration mismatch must not download");
        Assert.That(manager.LastFailure("mb-live-mgr-2"), Does.Contain("wrong length"));
    }
}
