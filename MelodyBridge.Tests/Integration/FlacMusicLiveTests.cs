using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Infrastructure.SearchProviders;
using MelodyBridge.Tests;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The flac.music.hi.cn fallback against the real site: search, getUrl,
/// direct audio, and the three formats the site advertises. This service is
/// independent of the Tidal account pool, so it is the more stable live
/// check for the whole download path.
/// </summary>
[TestFixture]
[Category("Live")]
public class FlacMusicLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-flacmusic-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static FlacMusicDownloader Downloader()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var client = new FlacMusicClient(http, NullLogger<FlacMusicClient>.Instance);
        return new FlacMusicDownloader(
            client, new StubPluginConfig(), NullLogger<FlacMusicDownloader>.Instance);
    }

    private static FlacMusicSearchProvider Search()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var client = new FlacMusicClient(http, NullLogger<FlacMusicClient>.Instance);
        return new FlacMusicSearchProvider(
            new FlacMusicSession(client, new StubPluginConfig(),
                NullLogger<FlacMusicSession>.Instance),
            NullLogger<FlacMusicSearchProvider>.Instance);
    }

    private static TrackQuery Query(DownloadQuality quality) => new(
        "AC/DC", "Back In Black", quality, TimeSpan.FromSeconds(255));

    [Test]
    public async Task Search_ACDC_FindsLosslessAndBothMp3Tiers()
    {
        var search = Search();
        var lossless = await search.SearchAsync("flacmusic", Query(new DownloadQuality(AudioFormat.Flac)));
        var high = await search.SearchAsync("flacmusic", Query(new DownloadQuality(AudioFormat.Auto, null, 320)));
        var small = await search.SearchAsync("flacmusic", Query(new DownloadQuality(AudioFormat.Auto, null, 160)));

        Assert.That(lossless, Is.Not.Null, "the site must know Back in Black");
        Assert.That(lossless!.SourceUrl, Does.Contain("/track/"));
        Assert.That(high, Is.Not.Null);
        Assert.That(high!.BitrateKbps, Is.EqualTo(320));
        Assert.That(small, Is.Not.Null);
        Assert.That(small!.BitrateKbps, Is.EqualTo(128));
    }

    [Test]
    public async Task Download_Flac_Produces16Bit44100Flac()
    {
        var search = Search();
        var downloader = Downloader();
        var hit = await search.SearchAsync("flacmusic", Query(new DownloadQuality(AudioFormat.Flac)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds a FLAC tier");

        var result = await downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-flacmusic-flac",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".flac"));
        var facts = await FfprobeAsync(result.FilePath!);
        Assert.That(facts.Codec, Is.EqualTo("flac"));
        Assert.That(facts.SampleRate, Is.EqualTo(44100));
        Assert.That(facts.BitsPerSample, Is.EqualTo(16));
        Assert.That(facts.DurationSeconds, Is.InRange(245, 265));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(800, 1300));
    }

    [Test]
    public async Task Download_HighMp3_Is320()
    {
        var search = Search();
        var downloader = Downloader();
        var hit = await search.SearchAsync("flacmusic", Query(new DownloadQuality(AudioFormat.Auto, null, 320)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds a 320 tier");

        var result = await downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-flacmusic-320",
            new DownloadQuality(AudioFormat.Auto, null, 320));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".mp3"));
        var facts = await FfprobeAsync(result.FilePath!);
        Assert.That(facts.Codec, Is.EqualTo("mp3"));
        Assert.That(facts.DurationSeconds, Is.InRange(245, 265));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(290, 330));
    }

    [Test]
    public async Task Download_SmallLossyTier_IsAround128()
    {
        var search = Search();
        var downloader = Downloader();
        var hit = await search.SearchAsync("flacmusic", Query(new DownloadQuality(AudioFormat.Auto, null, 160)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds a 128 tier");

        var result = await downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-flacmusic-128",
            new DownloadQuality(AudioFormat.Auto, null, 160));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        // The site labels this tier mp3, but some Kuwo tracks serve an
        // ADTS AAC stream there (the getUrl response says aac/100). An
        // automatic quality request accepts either; an explicit MP3
        // request is rejected by the manager's format gate and retried on
        // the other platform's candidate.
        Assert.That(AudioSignature.DetectContainer(result.FilePath!),
            Is.EqualTo(".mp3").Or.EqualTo(".m4a"));
        var facts = await FfprobeAsync(result.FilePath!);
        Assert.That(facts.Codec, Is.EqualTo("mp3").Or.EqualTo("aac"));
        Assert.That(facts.DurationSeconds, Is.InRange(245, 270));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(85, 145));
    }

    [Test]
    public async Task DirectKuwoLink_IsResolvedById()
    {
        var downloader = Downloader();
        var result = await downloader.DownloadAsync(
            "https://www.kuwo.cn/play_detail/336887",
            _outDir, "mb-flacmusic-direct", new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(AudioSignature.DetectContainer(result.FilePath!), Is.EqualTo(".flac"));
        Assert.That(BitrateProbe.MeasureKbps(result.FilePath!), Is.InRange(800, 1300));
    }

    private sealed record Facts(string Codec, int SampleRate, int? BitsPerSample, double DurationSeconds);

    private static async Task<Facts> FfprobeAsync(string file)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = "-v error -select_streams a:0 "
                + "-show_entries stream=codec_name,sample_rate,bits_per_raw_sample:format=duration "
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

        return new Facts(
            Value("codec_name") ?? "",
            int.TryParse(Value("sample_rate"), out var rate) ? rate : 0,
            int.TryParse(Value("bits_per_raw_sample"), out var bits) ? bits : null,
            double.TryParse(Value("duration"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds) ? seconds : 0);
    }
}
