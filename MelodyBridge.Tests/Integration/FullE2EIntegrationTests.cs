using System.Diagnostics;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;
using MelodyBridge.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Full end-to-end integration tests that search for a real copyright-free song on every provider,
/// download the result, and verify the downloaded file is valid audio.
///
/// These tests hit external APIs and require network access —
/// they are marked [Explicit] and will NOT run during normal CI.
/// Run manually with:
///   dotnet test --filter "Category=E2E"
/// </summary>
[TestFixture]
[Category("E2E")]
[Explicit]
public class FullE2EIntegrationTests
{
    private static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "MelodyBridge_E2E_Test");
    private static readonly TrackQuality Quality320 = new(320, MediaType.MP3);
    private static readonly TrackQuality Quality128 = new(128, MediaType.MP3);
    private static readonly TrackQuality QualityFlac16 = new(16, MediaType.FLAC);
    private static readonly TrackQuality QualityFlac24 = new(24, MediaType.FLAC);
    private static readonly TrackQuality QualityAac320 = new(320, MediaType.AAC);

    private SquidWtfProvider _squid = null!;
    private LucidaProvider _lucida = null!;
    private DoubleDoubleProvider _doubleDouble = null!;
    private MonochromeProvider _monochrome = null!;

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);

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

    // ────────────────────────────────────────────────────────
    //  E2E: SEARCH → PICK FIRST RESULT → DOWNLOAD → VERIFY
    // ────────────────────────────────────────────────────────

    [Test]
    [Timeout((int)2.5 * 60 * 1000)]
    public async Task E2E_Monochrome_SearchThenDownload()
    {
        // Search for a copyright-free song
        var results = await RunSearch(_monochrome, TestSongs.Beethoven5.Query);

        Assert.That(results, Is.Not.Empty, "Monochrome should return at least one result for Beethoven");

        var best = results[0];
        TestContext.Out.WriteLine($"Best result: {best.Title} — {best.Artist} [{best.Url}]");

        // Download it
        var quality = PickBestQuality(best.AvailableQualities, QualityAac320);
        var dlResult = await _monochrome.DownloadAsync(best.Url, quality, OutputDir);

        Assert.That(dlResult.Success, Is.True, $"Download failed: {dlResult.ErrorMessage}");
        await AssertValidAudioFile(dlResult.FilePath, "AAC", "MP3", "FLAC");
    }

    [Test]
    [Timeout((int)2.5 * 60 * 1000)]
    public async Task E2E_SquidWtf_SearchThenDownload()
    {
        var results = await RunSearch(_squid, TestSongs.Beethoven5.Query);

        Assert.That(results, Is.Not.Empty, "SquidWtf should return at least one result for Beethoven");

        var best = results[0];
        TestContext.Out.WriteLine($"Best result: {best.Title} — {best.Artist} [{best.Url}]");

        var quality = PickBestQuality(best.AvailableQualities, Quality320);
        var dlResult = await _squid.DownloadAsync(best.Url, quality, OutputDir);

        Assert.That(dlResult.Success, Is.True, $"Download failed: {dlResult.ErrorMessage}");
        await AssertValidAudioFile(dlResult.FilePath, "MP3", "FLAC", "AAC");
    }

    [Test]
    [Timeout((int)2.5 * 60 * 1000)]
    public async Task E2E_Lucida_SearchThenDownload()
    {
        var results = await RunSearch(_lucida, TestSongs.NightOwl.Query);

        Assert.That(results, Is.Not.Empty, "Lucida should return at least one result for Night Owl");

        var best = results[0];
        TestContext.Out.WriteLine($"Best result: {best.Title} — {best.Artist} [{best.Url}]");

        var quality = PickBestQuality(best.AvailableQualities, Quality320);
        var dlResult = await _lucida.DownloadAsync(best.Url, quality, OutputDir);

        Assert.That(dlResult.Success, Is.True, $"Download failed: {dlResult.ErrorMessage}");
        await AssertValidAudioFile(dlResult.FilePath, "MP3", "FLAC", "AAC");
    }

    [Test]
    [Timeout((int)2.5 * 60 * 1000)]
    public async Task E2E_DoubleDouble_SearchThenDownload()
    {
        var results = await RunSearch(_doubleDouble, TestSongs.NightOwl.Query);

        Assert.That(results, Is.Not.Empty, "DoubleDouble should return at least one result for Night Owl");

        var best = results[0];
        TestContext.Out.WriteLine($"Best result: {best.Title} — {best.Artist} [{best.Url}]");

        var quality = PickBestQuality(best.AvailableQualities, Quality320);
        var dlResult = await _doubleDouble.DownloadAsync(best.Url, quality, OutputDir);

        Assert.That(dlResult.Success, Is.True, $"Download failed: {dlResult.ErrorMessage}");
        await AssertValidAudioFile(dlResult.FilePath, "MP3", "FLAC", "AAC", "M4A");
    }

    // ────────────────────────────────────────────────────────
    //  STATIC-URL DOWNLOAD TESTS (known track URLs)
    // ────────────────────────────────────────────────────────

    [Test]
    [Timeout(120_000)]
    public async Task SquidWtf_Download_KnownQobuzTrack()
    {
        // Qobuz public-domain classical track
        var result = await _squid.DownloadAsync(
            "https://www.qobuz.com/us-en/track/12345678",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3", "FLAC", "AAC");
    }

    [Test]
    [Timeout(120_000)]
    public async Task Monochrome_Download_KnownTidalTrack()
    {
        // TIDAL classical track via Monochrome
        var result = await _monochrome.DownloadAsync(
            "https://tidal.com/browse/track/123456789",
            QualityAac320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "AAC", "MP3", "FLAC");
    }

    [Test]
    [Timeout(120_000)]
    public async Task DoubleDouble_Download_KnownAmazonTrack()
    {
        var result = await _doubleDouble.DownloadAsync(
            "https://music.amazon.com/tracks/B0123456789",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3", "FLAC", "AAC", "M4A");
    }

    [Test]
    [Timeout(120_000)]
    public async Task Lucida_Download_KnownDeezerTrack()
    {
        var result = await _lucida.DownloadAsync(
            "https://www.deezer.com/track/12345678",
            Quality320, OutputDir);
        Assert.That(result.Success, Is.True, result.ErrorMessage ?? "Unknown error");
        await AssertValidAudioFile(result.FilePath, "MP3", "FLAC", "AAC");
    }

    // ────────────────────────────────────────────────────────
    //  CROSS-PROVIDER CONSISTENCY
    // ────────────────────────────────────────────────────────

    [Test]
    [Timeout((int)3 * 60 * 1000)]
    public async Task AllProviders_CanSearchSameQuery()
    {
        var query = "Beethoven Symphony No 5";
        var allProviders = new IMusicProvider[] { _squid, _lucida, _doubleDouble, _monochrome };
        var allResults = new Dictionary<string, int>();

        foreach (var p in allProviders)
        {
            try
            {
                var results = await RunSearch(p, query);
                allResults[p.Name] = results.Count;
                TestContext.Out.WriteLine($"{p.Name}: {results.Count} results");
            }
            catch (Exception ex)
            {
                allResults[p.Name] = -1;
                TestContext.Out.WriteLine($"{p.Name}: ERROR — {ex.Message}");
            }
        }

        // At least 2 providers should return results
        var withResults = allResults.Count(kvp => kvp.Value > 0);
        Assert.That(withResults, Is.GreaterThanOrEqualTo(2),
            $"Expected at least 2 providers to return results, got {withResults}");
    }

    // ────────────────────────────────────────────────────────
    //  HELPERS
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Run a search with timeout protection.
    /// </summary>
    private static async Task<List<SearchResult>> RunSearch(IMusicProvider provider, string query)
    {
        using var cts = new CancellationTokenSource(ProviderTimeout);
        try
        {
            var task = provider.SearchAsync(query);
            // Wait with timeout
            await Task.WhenAny(task, Task.Delay(ProviderTimeout, cts.Token));
            if (!task.IsCompleted)
                throw new TimeoutException($"Search timed out after {ProviderTimeout.TotalSeconds}s");

            return task.Result.ToList();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Search was cancelled after {ProviderTimeout.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Pick the best available quality, falling back to a default.
    /// </summary>
    private static TrackQuality PickBestQuality(IReadOnlyList<TrackQuality> available, TrackQuality fallback)
    {
        if (available.Count == 0) return fallback;
        return available
            .OrderByDescending(q => q.Bitrate)
            .ThenByDescending(q => q.Format == MediaType.FLAC ? 1 : q.Format == MediaType.AAC ? 2 : 3)
            .First();
    }

    /// <summary>
    /// Verify a downloaded file exists and has valid audio headers.
    /// </summary>
    private static async Task AssertValidAudioFile(string? filePath, params string[] expectedFormats)
    {
        Assert.That(filePath, Is.Not.Null, "Download returned null file path");
        Assert.That(File.Exists(filePath!), Is.True, $"File does not exist: {filePath}");

        var fi = new FileInfo(filePath!);
        Assert.That(fi.Length, Is.GreaterThan(1024), $"File is too small ({fi.Length} bytes) — likely a stub or error page");

        var header = await ReadAudioHeaderAsync(filePath!);

        var knownFormats = new[] { "MP3", "FLAC", "OGG", "M4A", "AAC", "WAV", "AIFF", "WMA" };
        var anyMatch = knownFormats.Any(f =>
            header.Contains(f, StringComparison.OrdinalIgnoreCase));
        Assert.That(anyMatch, Is.True,
            $"File header '{header}' doesn't match any known audio format");

        // Additional check for expected format hint
        if (expectedFormats.Length > 0)
        {
            var hintMatch = expectedFormats.Any(f =>
                header.Contains(f, StringComparison.OrdinalIgnoreCase));
            Assert.That(hintMatch, Is.True,
                $"File header '{header}' doesn't contain any of: {string.Join(", ", expectedFormats)}");
        }
    }

    /// <summary>
    /// Read the first 64 bytes of a file and return a hex string + printable ASCII
    /// so we can identify the file type from its magic bytes.
    /// </summary>
    private static async Task<string> ReadAudioHeaderAsync(string filePath)
    {
        var buffer = new byte[64];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var read = await fs.ReadAsync(buffer.AsMemory(0, 64));

        var hex = Convert.ToHexString(buffer[..read]);
        var ascii = string.Concat(buffer[..read].Select(b => b >= 32 && b < 127 ? (char)b : '.'));
        return $"{hex} | {ascii}";
    }
}
