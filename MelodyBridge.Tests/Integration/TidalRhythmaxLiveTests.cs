using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Rhythmax;
using MelodyBridge.Infrastructure.Tidal;
using MelodyBridge.Tests;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Rhythmax and the public Tidal searcher against the real services. The
/// searcher needs no account; the download uses the public hifi API's own
/// account pool, so a day when that pool has no active account is skipped
/// with the service's own reason instead of being reported as a code bug.
/// </summary>
[TestFixture]
[Category("Live")]
public class TidalRhythmaxLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-rhythmax-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static TidalPublicCatalog Catalog()
        => new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<TidalPublicCatalog>.Instance);

    private static RhythmaxDownloader Downloader(StubPluginConfig? config = null)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var client = new RhythmaxClient(http,
            Environment.GetEnvironmentVariable("RHYTHMAX_BASE_URL") ?? RhythmaxClient.DefaultBaseUrl,
            NullLogger<RhythmaxClient>.Instance);
        return new RhythmaxDownloader(client, config ?? new StubPluginConfig(),
            NullLogger<RhythmaxDownloader>.Instance);
    }

    private static RhythmaxClient Client()
        => new(new HttpClient { Timeout = TimeSpan.FromMinutes(30) },
            Environment.GetEnvironmentVariable("RHYTHMAX_BASE_URL") ?? RhythmaxClient.DefaultBaseUrl,
            NullLogger<RhythmaxClient>.Instance);

    /// <summary>
    /// The public account pool flips between FULL and DEAD within seconds.
    /// Give it a short window to come back before skipping, then start the
    /// download immediately: checking once and downloading later misses the
    /// window that just opened.
    /// </summary>
    private static async Task RequireFullServiceAsync()
    {
        string? reason = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var (ready, why) = await Client().CheckHealthAsync();
            if (ready) return;
            reason = why;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        Assert.Ignore(reason ?? "Rhythmax is not ready");
    }

    [Test]
    public async Task PublicTidalSearch_ResolvesAKnownIsrc()
    {
        var result = await Catalog().SearchByIsrcAsync("AUAP08000046", "US", 10);

        if (result.Status == TidalPublicCatalog.SearchStatus.RateLimited)
            Assert.Ignore(result.Message ?? "Tidal rate limited the public search");

        Assert.That(result.Status, Is.EqualTo(TidalPublicCatalog.SearchStatus.Ok),
            result.Message);
        Assert.That(result.Tracks, Is.Not.Empty,
            "Back in Black (AUAP08000046) must be in Tidal's catalogue");
        Assert.That(result.Tracks.Any(t => t.Isrc == "AUAP08000046"), Is.True);
        Assert.That(result.Tracks.Any(t => t.Title.Contains("Back In Black", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public async Task PublicTidalSearch_TextFallbackFindsTheTrackWithoutAnIsrc()
    {
        var result = await Catalog().SearchTextAsync("AC/DC Back In Black", "US", 10);

        if (result.Status == TidalPublicCatalog.SearchStatus.RateLimited)
            Assert.Ignore(result.Message ?? "Tidal rate limited the public search");

        Assert.That(result.Status, Is.EqualTo(TidalPublicCatalog.SearchStatus.Ok),
            result.Message);
        Assert.That(result.Tracks, Is.Not.Empty);
        Assert.That(result.Tracks.Any(t => t.Artist.Contains("AC/DC", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public async Task Download_LosslessTier_ProducesARealFlac()
    {
        await RequireFullServiceAsync();
        var search = await Catalog().SearchByIsrcAsync("AUAP08000046", "US", 10);
        var track = search.Tracks.FirstOrDefault(t => t.Isrc == "AUAP08000046");
        Assert.That(track, Is.Not.Null, "precondition: the public search finds the track");

        var downloader = Downloader();
        var result = await DownloadWithRetryAsync(downloader, track!.SourceUrl,
            _outDir, "mb-rhythmax-flac", new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".flac"));
        var facts = await FfprobeAsync(result.FilePath!);
        Assert.That(facts.Codec, Is.EqualTo("flac"));
        Assert.That(facts.DurationSeconds, Is.InRange(240, 280));
        Assert.That(facts.BitsPerSample, Is.EqualTo(16));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(700, 1400));
    }

    [Test]
    public async Task Download_HighAacTier_ProducesA320M4a()
    {
        await RequireFullServiceAsync();
        var search = await Catalog().SearchByIsrcAsync("AUAP08000046", "US", 10);
        var track = search.Tracks.FirstOrDefault(t => t.Isrc == "AUAP08000046");
        Assert.That(track, Is.Not.Null, "precondition: the public search finds the track");

        var downloader = Downloader();
        var result = await DownloadWithRetryAsync(downloader, track!.SourceUrl,
            _outDir, "mb-rhythmax-aac", new DownloadQuality(AudioFormat.Aac, null, 320));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".m4a"));
        var facts = await FfprobeAsync(result.FilePath!);
        Assert.That(facts.Codec, Is.EqualTo("aac"));
        Assert.That(facts.DurationSeconds, Is.InRange(240, 280));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(250, 340));
    }

    [Test]
    public async Task Download_LowAacTier_ProducesA96M4a()
    {
        await RequireFullServiceAsync();
        var search = await Catalog().SearchByIsrcAsync("AUAP08000046", "US", 10);
        var track = search.Tracks.FirstOrDefault(t => t.Isrc == "AUAP08000046");
        Assert.That(track, Is.Not.Null, "precondition: the public search finds the track");

        var downloader = Downloader();
        var result = await DownloadWithRetryAsync(downloader, track!.SourceUrl,
            _outDir, "mb-rhythmax-low", new DownloadQuality(AudioFormat.Aac, null, 96));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".m4a"));
        var facts = await FfprobeAsync(result.FilePath!);
        Assert.That(facts.Codec, Is.EqualTo("aac"));
        Assert.That(facts.DurationSeconds, Is.InRange(240, 280));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(64, 150));
    }

    /// <summary>
    /// The public API returns "no playback slot" (its own Tidal 429) when
    /// the account pool is between requests. That is the service pacing
    /// itself, not a broken download: retry a few times before calling the
    /// live check unavailable.
    /// </summary>
    private static async Task<DownloaderDownloadResult> DownloadWithRetryAsync(
        RhythmaxDownloader downloader, string sourceUrl, string outputDirectory,
        string melodyId, DownloadQuality quality)
    {
        DownloaderDownloadResult? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            last = await downloader.DownloadAsync(sourceUrl, outputDirectory, melodyId, quality);
            if (last.Success) return last;
            if (last.ErrorMessage?.Contains("429", StringComparison.Ordinal) != true
                && last.ErrorMessage?.Contains("playback slot", StringComparison.OrdinalIgnoreCase) != true)
                return last;
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        Assert.Ignore("Rhythmax kept answering Tidal 429: " + last?.ErrorMessage);
        return last!;
    }

    private sealed record Facts(string Codec, double DurationSeconds, int? BitsPerSample);

    private static async Task<Facts> FfprobeAsync(string file)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = "-v error -select_streams a:0 "
                + "-show_entries stream=codec_name,bits_per_raw_sample:format=duration "
                + "-of default=noprint_wrappers=1 \"" + file + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        string? Value(string key)
            => output.Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?
                [(key.Length + 1)..];

        var codec = Value("codec_name") ?? "";
        var duration = double.TryParse(Value("duration"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        var bits = int.TryParse(Value("bits_per_raw_sample"), out var parsed) ? parsed : (int?)null;
        return new Facts(codec, duration, bits);
    }
}
