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
        var hit = await _downloader.SearchAsync(new TrackQuery("Ludwig van Beethoven", "Für Elise", MelodyBridge.Core.DownloadQuality.Any));
        Assert.That(hit, Is.Not.Null, "ytsearch must find this well-known track");
        Assert.That(hit!.SourceUrl, Does.StartWith("https://www.youtube.com/watch?v="));
        Assert.That(hit.Title, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Search_RanksTheOfficialTrack_NotTheLyricVideo()
    {
        // The top-5 results for this query contain lyric channels and
        // remixes; the official upload must win the ranking.
        var hit = await _downloader.SearchAsync(new TrackQuery("Rick Astley", "Never Gonna Give You Up", MelodyBridge.Core.DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Title, Does.Contain("Never Gonna Give You Up"));
        Assert.That(hit.Artist, Does.Contain("Rick Astley"),
            "the ranking must prefer the artist's own upload over cover/lyric channels");
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(hit.Duration, Is.Not.Null);
        Assert.That(hit.Duration!.Value.TotalSeconds, Is.InRange(195, 235),
            "the official track runs about 3:33");
    }

    [Test]
    public async Task Search_WrongLengthExpectation_ReturnsTheCandidate_ForTheManagerToReject()
    {
        // The ranker treats a gross mismatch as a last resort: the
        // candidate still surfaces so the waterfall can name the reason
        // ("wrong length"); the manager's duration gate refuses to
        // download it.
        var hit = await _downloader.SearchAsync(new TrackQuery(
            "Rick Astley", "Never Gonna Give You Up",
            MelodyBridge.Core.DownloadQuality.Any, ExpectedDuration: TimeSpan.FromSeconds(100)));

        Assert.That(hit, Is.Not.Null, "the last-resort candidate surfaces");
        Assert.That(hit!.Duration!.Value.TotalSeconds, Is.GreaterThan(180),
            "and its length really is wrong for the expectation");
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
        var hit = await _downloader.SearchAsync(new TrackQuery("Ludwig van Beethoven", "Für Elise", MelodyBridge.Core.DownloadQuality.Any));
        Assert.That(hit, Is.Not.Null, "precondition: search finds the track");

        var result = await _downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-test-melody-123");

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(System.IO.File.Exists(result.FilePath), Is.True, "reported file must exist");

        // The MELODY_ID tag must be readable from the actual file.
        // The manager writes the id, not the plugin, since the tagging
        // refactor; this direct-plugin test applies that same step
        // before verifying the tag survives inside the real bytes.
        MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(result.FilePath!, "mb-test-melody-123");
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
        // Invariant culture: ffprobe prints a dot decimal; a Dutch locale
        // would read "213.58" as 213,580,000.
        Assert.That(double.TryParse(duration.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds), Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.GreaterThan(60).And.LessThan(600), "Für Elise is ~3-4 minutes");
    }

    [Test]
    public async Task Download_IsAudioOnly_NeverAVideoFile()
    {
        var hit = await _downloader.SearchAsync(new TrackQuery("Rick Astley", "Never Gonna Give You Up", MelodyBridge.Core.DownloadQuality.Any));
        Assert.That(hit, Is.Not.Null, "precondition: search finds the track");

        var result = await _downloader.DownloadAsync(hit!.SourceUrl!, _outDir, "mb-audio-only");
        Assert.That(result.Success, Is.True, result.ErrorMessage);

        // yt-dlp's default format chain would download the full video
        // (tens of megabytes); the plugin must pick the audio stream only.
        var streamTypes = await FfprobeAsync("-v error -show_entries stream=codec_type -of csv=p=0", result.FilePath!);
        var types = streamTypes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(types, Is.EqualTo(new[] { "audio" }),
            "exactly one audio stream, no video stream");
        Assert.That(new FileInfo(result.FilePath!).Length, Is.LessThan(15 * 1024 * 1024),
            "an audio-only download stays a few megabytes, not tens");
    }

    [Test]
    public async Task ParallelDownloads_IntoOneFolder_ProduceDistinctIntactFiles()
    {
        var tracks = new[]
        {
            ("Ludwig van Beethoven", "Für Elise"),
            ("Frédéric Chopin", "Nocturne in E-flat major, Op. 9 No. 2"),
            ("Erik Satie", "Gymnopédie No. 1"),
        };

        var hits = new List<DownloaderSearchHit>();
        foreach (var (artist, title) in tracks)
        {
            var hit = await _downloader.SearchAsync(new TrackQuery(artist, title, MelodyBridge.Core.DownloadQuality.Any));
            Assert.That(hit, Is.Not.Null, $"search must find {title}");
            hits.Add(hit!);
        }

        // Three downloads race into the same folder, like the coordinator's
        // parallel workers do: every file must land intact under its own
        // name, never clobbering another track.
        var tasks = hits.Select((hit, index) =>
            _downloader.DownloadAsync(hit.SourceUrl!, _outDir, $"mb-parallel-{index}")).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.That(results.Select(r => r.Success), Is.All.True,
            string.Join("; ", results.Select(r => r.ErrorMessage)));
        var paths = results.Select(r => r.FilePath!).ToList();
        Assert.That(paths.Distinct().Count(), Is.EqualTo(3), "no two downloads share a file");
        foreach (var path in paths)
        {
            Assert.That(System.IO.File.Exists(path), Is.True);
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(100 * 1024),
                "each parallel download produced real bytes");
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
