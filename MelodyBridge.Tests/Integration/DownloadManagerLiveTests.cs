using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Tagging;
using MelodyBridge.Tests.Infrastructure;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live waterfall test: the app's own DownloadManager over the app's own
/// composition (every shipped plugin, resolved from AddMelodyBridge), real
/// network, real file validation. Proves the full path a playlist track
/// takes: search (ranked), duration gate, download, signature check,
/// MELODY_ID tagging. No test-local plugin list: if the shipped waterfall
/// changes, this fixture follows it.
/// </summary>
[TestFixture]
[Category("Live")]
public class DownloadManagerLiveTests
{
    private AppDownloadHost _app = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _app = AppDownloadHost.Create();
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-mgr-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _app.Dispose();
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task DownloadTrack_ThroughTheRealWaterfall_WritesTaggedAudio()
    {
        var manager = _app.DownloadManager;

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
                ExpectedDuration: TimeSpan.FromSeconds(214)), _outDir, "mb-live-mgr-1");

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
        var manager = _app.DownloadManager;

        // The playlist claims 100 seconds; every real source of this track
        // runs ~3:33. Downloading one anyway would be the wrong track.
        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
                ExpectedDuration: TimeSpan.FromSeconds(100)), _outDir, "mb-live-mgr-2");

        Assert.That(path, Is.Null, "a gross duration mismatch must not download");
        Assert.That(manager.LastFailure("mb-live-mgr-2"), Does.Contain("wrong length"));
    }
}
