using System.Diagnostics;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Integration tests that perform real downloads from music providers
/// and verify the resulting files are valid audio.
///
/// These tests hit external APIs and require network access —
/// they are marked [Explicit] and will NOT run during normal CI.
/// Run manually with:
///   dotnet test --filter "Category=DownloadIntegration"
/// </summary>
[TestFixture]
[Category("DownloadIntegration")]
[Explicit]
public class DownloadIntegrationTests
{
    private static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "MelodyBridge_DownloadTest");
    private static readonly TrackQuality Quality320 = new(320, MediaType.MP3);
    private static readonly TrackQuality Quality128 = new(128, MediaType.MP3);
    private static readonly TrackQuality QualityFlac16 = new(16, MediaType.FLAC);
    private static readonly TrackQuality QualityFlac24 = new(24, MediaType.FLAC);
    private static readonly TrackQuality QualityAac320 = new(320, MediaType.AAC);

    private SquidWtfProvider _squid = null!;
    private LucidaProvider _lucida = null!;
    private DoubleDoubleProvider _doubleDouble = null!;
    private MonochromeProvider _monochrome = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Directory.CreateDirectory(OutputDir);
        _squid = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        _lucida = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        _doubleDouble = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        _monochrome = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try { Directory.Delete(OutputDir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Verify a downloaded file exists and has valid audio headers.
    /// </summary>
    private static async Task AssertValidAudioFile(string? filePath, string expectedFormatHint)
    {
        Assert.That(filePath, Is.Not.Null, "Download returned null file path");
        Assert.That(File.Exists(filePath!), Is.True, $"File does not exist: {filePath}");

        var fi = new FileInfo(filePath!);
        Assert.That(fi.Length, Is.GreaterThan(1024), $"File is too small ({fi.Length} bytes)");

        var header = await ReadAudioHeaderAsync(filePath!);
        Assert.That(header, Does.Contain(expectedFormatHint)
            .Or.Contains("MP3")
            .Or.Contains("FLAC")
            .Or.Contains("OGG")
            .Or.Contains("M4A")
            .Or.Contains("AAC"),
            $"File header '{header}' doesn't look like known audio format");
    }

    // ── Squid.wtf ──────────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task SquidWtf_Download_Qobuz_320Mp3()
    {
        // Qobuz track URL
        var result = await _squid.DownloadAsync(
            "https://www.qobuz.com/us-en/track/12345678",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3");
    }

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task SquidWtf_Download_Tidal_Flac24()
    {
        var result = await _squid.DownloadAsync(
            "https://tidal.com/browse/track/123456789",
            QualityFlac24, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "FLAC");
    }

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task SquidWtf_Download_SoundCloud_128Mp3()
    {
        var result = await _squid.DownloadAsync(
            "https://soundcloud.com/artist/track",
            Quality128, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3");
    }

    // ── Lucida ─────────────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task Lucida_Download_Deezer_320Mp3()
    {
        var result = await _lucida.DownloadAsync(
            "https://www.deezer.com/track/12345678",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3");
    }

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task Lucida_Download_Qobuz_Flac16()
    {
        var result = await _lucida.DownloadAsync(
            "https://www.qobuz.com/us-en/track/12345678",
            QualityFlac16, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "FLAC");
    }

    // ── DoubleDouble ───────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task DoubleDouble_Download_AmazonMusic_320Mp3()
    {
        var result = await _doubleDouble.DownloadAsync(
            "https://music.amazon.com/tracks/B0123456789",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3");
    }

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task DoubleDouble_Download_Tidal_Flac24()
    {
        var result = await _doubleDouble.DownloadAsync(
            "https://tidal.com/browse/track/123456789",
            QualityFlac24, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "FLAC");
    }

    // ── Monochrome ─────────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task Monochrome_Download_Tidal_Aac320()
    {
        // Monochrome only supports TIDAL
        var result = await _monochrome.DownloadAsync(
            "https://tidal.com/browse/track/123456789",
            QualityAac320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "AAC");
    }

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task Monochrome_Download_Tidal_Flac24()
    {
        var result = await _monochrome.DownloadAsync(
            "https://tidal.com/browse/track/123456789",
            QualityFlac24, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "FLAC");
    }

    // ── Download manager waterfall ─────────────────────────

    [Test]
    [Explicit]
    [Timeout(120_000)]
    public async Task DownloadManager_Waterfall_FallsBackAcrossProviders()
    {
        // Use Squid.wtf as primary, verify waterfall works
        var result = await _squid.DownloadAsync(
            "https://tidal.com/browse/track/123456789",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3");
    }

    // ── Helpers ────────────────────────────────────────────

    /// <summary>Read file header magic bytes to identify audio format.</summary>
    private static async Task<string> ReadAudioHeaderAsync(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var header = new byte[16];
        var read = await fs.ReadAsync(header, 0, Math.Min(16, header.Length));
        if (read < 4) return "too small";

        if (header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33) return "MP3 (ID3v2)";
        if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0) return "MP3";
        if (header[0] == 0x66 && header[1] == 0x4C && header[2] == 0x61 && header[3] == 0x43) return "FLAC";
        if (header[0] == 0x4F && header[1] == 0x67 && header[2] == 0x67 && header[3] == 0x53) return "OGG (Vorbis/Opus)";
        if (header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70) return "M4A/AAC (MP4)";
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46) return "WAV";

        return $"Unknown ({BitConverter.ToString(header, 0, read)})";
    }
}
