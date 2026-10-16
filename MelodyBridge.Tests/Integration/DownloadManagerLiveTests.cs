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

        // The playlist claims 60 seconds; every real source of this track
        // runs 2:25 or longer (the API even carries a 171s edit), so every
        // candidate is a gross mismatch. Downloading one anyway would be
        // the wrong track. 100s would NOT be enough: 171/100 lands inside
        // the plausible window.
        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
                ExpectedDuration: TimeSpan.FromSeconds(60)), _outDir, "mb-live-mgr-2");

        Assert.That(path, Is.Null, "a gross duration mismatch must not download");
        Assert.That(manager.LastFailure("mb-live-mgr-2"), Does.Contain("wrong length"));
    }

    [Test]
    public async Task DownloadTrack_FlacRequest_ThroughMonochromeAlone_StreamsLossless()
    {
        // Force the lossless path: every other plugin off, Monochrome on.
        // The waterfall is only ever changed the way a user changes it,
        // through the registry (enable/disable), never a test-local list.
        var registry = _app.Registry;
        foreach (var plugin in registry.GetAll().Where(p => p.Id != "monochrome"))
            await registry.SetEnabledAsync(plugin.Id, false);

        try
        {
            var manager = _app.DownloadManager;
            var path = await manager.DownloadTrackAsync(
                new TrackQuery("Rick Astley", "Never Gonna Give You Up",
                    new DownloadQuality(AudioFormat.Flac),
                    ExpectedDuration: TimeSpan.FromSeconds(214)),
                _outDir, "mb-live-mgr-flac");

            Assert.That(path, Is.Not.Null, manager.LastFailure("mb-live-mgr-flac"));
            Assert.That(TaglibHelper.ReadMelodyId(path!), Is.EqualTo("mb-live-mgr-flac"),
                "the manager's one tagging point wrote the id into the real file");

            // Lossless, and nothing else: the file really came from the
            // official API's FLAC stream, not an error page or a lossy rip.
            var codec = await FfprobeAsync("-v error -show_entries stream=codec_name -of csv=p=0", path!);
            var codecs = codec.Split(new[] { ',', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.That(codecs, Is.All.EqualTo("flac"));

            var duration = await FfprobeAsync("-v quiet -show_entries format=duration -of csv=p=0", path!);
            Assert.That(double.TryParse(duration.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds), Is.True);
            Assert.That(seconds, Is.InRange(195, 235), "the 3:33 studio recording");
        }
        finally
        {
            foreach (var plugin in registry.GetAll())
                await registry.SetEnabledAsync(plugin.Id, true);
        }
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
