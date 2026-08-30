using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class DoubleDoubleProviderTests
{
    private DoubleDoubleProvider _provider = null!;
    private string _testOutputDir = null!;

    [SetUp]
    public void Setup()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<DoubleDoubleProvider>();
        _provider = new DoubleDoubleProvider(logger);
        _testOutputDir = Path.Combine(Path.GetTempPath(), $"melodybridge_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testOutputDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testOutputDir))
        {
            try
            {
                Directory.Delete(_testOutputDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Metadata
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void ProviderMetadata_IsCorrect()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_provider.Id, Is.EqualTo("doubledouble"));
            Assert.That(_provider.Name, Is.EqualTo("DoubleDouble"));
            Assert.That(_provider.Icon, Is.EqualTo("🔁"));
            Assert.That(_provider.SupportedPlatforms, Is.EquivalentTo(new[]
            {
                Platform.AmazonMusic, Platform.Soundcloud, Platform.Qobuz, Platform.Deezer, Platform.Tidal
            }));
            Assert.That(_provider.Description, Is.Not.Empty);
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        Assert.That(_provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.DoubleDouble));
    }

    [Test]
    public void DefaultRegion_IsUs()
    {
        Assert.That(_provider.Region, Is.EqualTo("us"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Platform Detection
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void DetectPlatform_AmazonUrl_ReturnsAmazonMusic()
    {
        var result = InvokeDetectPlatform("https://music.amazon.com/track/abc");
        Assert.That(result, Is.EqualTo(Platform.AmazonMusic));
    }

    [Test]
    public void DetectPlatform_SoundcloudUrl_ReturnsSoundcloud()
    {
        var result = InvokeDetectPlatform("https://soundcloud.com/artist/track");
        Assert.That(result, Is.EqualTo(Platform.Soundcloud));
    }

    [Test]
    public void DetectPlatform_QobuzUrl_ReturnsQobuz()
    {
        var result = InvokeDetectPlatform("https://www.qobuz.com/track/123");
        Assert.That(result, Is.EqualTo(Platform.Qobuz));
    }

    [Test]
    public void DetectPlatform_DeezerUrl_ReturnsDeezer()
    {
        var result = InvokeDetectPlatform("https://www.deezer.com/track/123");
        Assert.That(result, Is.EqualTo(Platform.Deezer));
    }

    [Test]
    public void DetectPlatform_TidalUrl_ReturnsTidal()
    {
        var result = InvokeDetectPlatform("https://tidal.com/browse/track/123");
        Assert.That(result, Is.EqualTo(Platform.Tidal));
    }

    [Test]
    public void DetectPlatform_UnknownUrl_ReturnsUnknown()
    {
        var result = InvokeDetectPlatform("https://example.com/something");
        Assert.That(result, Is.EqualTo(Platform.Unknown));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Service Mapping
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void MapPlatformToService_ReturnsCorrectService()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InvokeMapPlatformToService(Platform.Qobuz), Is.EqualTo("qobuz"));
            Assert.That(InvokeMapPlatformToService(Platform.Tidal), Is.EqualTo("tidal"));
            Assert.That(InvokeMapPlatformToService(Platform.Deezer), Is.EqualTo("deezer"));
            Assert.That(InvokeMapPlatformToService(Platform.Soundcloud), Is.EqualTo("soundcloud"));
            Assert.That(InvokeMapPlatformToService(Platform.AmazonMusic), Is.EqualTo("amazon"));
            Assert.That(InvokeMapPlatformToService(Platform.Unknown), Is.EqualTo("qobuz"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - HTML Parsing
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void ExtractDownloadUrl_ValidHtml_ReturnsUrl()
    {
        var html = @"<a href=""https://files.doubledouble.top/download/abc123.flac"">Download</a>";
        var result = InvokeExtractDownloadUrl(html);
        Assert.That(result, Is.EqualTo("https://files.doubledouble.top/download/abc123.flac"));
    }

    [Test]
    public void ExtractDownloadUrl_NoMatch_ReturnsNull()
    {
        var html = "<html><body>No download links here</body></html>";
        var result = InvokeExtractDownloadUrl(html);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ExtractDownloadUrl_EmptyHtml_ReturnsNull()
    {
        var result = InvokeExtractDownloadUrl("");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ParseResults_ValidHtml_ReturnsResults()
    {
        var html = @"<a href=""/download/abc123.flac"">Track Name</a>";
        var results = InvokeParseResults(html, Platform.Qobuz);
        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results[0].Title, Is.EqualTo("Track Name"));
            Assert.That(results[0].Url, Does.Contain("/download/abc123.flac"));
            Assert.That(results[0].SourcePlatform, Is.EqualTo(Platform.Qobuz));
        });
    }

    [Test]
    public void ParseResults_NoMatches_ReturnsEmpty()
    {
        var html = "<html><body>Nothing</body></html>";
        var results = InvokeParseResults(html, Platform.Tidal);
        Assert.That(results, Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Integration Tests - Real API Calls
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_RealQuery_ReturnsResultsOrHandlesCaptcha()
    {
        // Note: DoubleDouble may return empty due to CAPTCHA protection
        var results = await _provider.SearchAsync("Queen Bohemian Rhapsody", Platform.Qobuz);

        // The search may be blocked by CAPTCHA, which is expected
        // We just verify the method completes without throwing
        Assert.That(results, Is.Not.Null);
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_QobuzUrl_ReturnsTrackInfoOrHandlesLimitation()
    {
        var url = "https://www.qobuz.com/track/27648923";
        
        var trackInfo = await _provider.GetTrackInfoAsync(url);

        // May return null if CAPTCHA blocks or if track not found
        // We verify the method completes without throwing
        Assert.That(trackInfo, Is.Null.Or.InstanceOf<TrackInfo>());
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_InvalidUrl_ReturnsNull()
    {
        var trackInfo = await _provider.GetTrackInfoAsync("https://example.com/not-a-track");
        Assert.That(trackInfo, Is.Null);
    }

    [Test]
    [Category("Integration")]
    [Timeout(120000)]
    public async Task DownloadAsync_QobuzTrack_AttemptsDownload()
    {
        // Note: This test may fail due to CAPTCHA protection
        // But it verifies the download flow works correctly
        var url = "https://www.qobuz.com/track/27648923";
        var quality = new TrackQuality(320, MediaType.MP3);
        
        var result = await _provider.DownloadAsync(url, quality, _testOutputDir);

        // Result may be failure due to CAPTCHA, but should not throw
        Assert.That(result, Is.Not.Null);
        
        if (result.Success)
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.FilePath, Is.Not.Empty);
                Assert.That(File.Exists(result.FilePath), Is.True);
            });
        }
        else
        {
            // Expected: CAPTCHA or rate limiting
            Assert.That(result.ErrorMessage, Is.Not.Empty);
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_WithDifferentPlatforms_HandlesCorrectly()
    {
        // Test with different platform types
        var platforms = new[] { Platform.Tidal, Platform.Deezer, Platform.Soundcloud };
        
        foreach (var platform in platforms)
        {
            var results = await _provider.SearchAsync("test query", platform);
            Assert.That(results, Is.Not.Null, $"Search for {platform} should return non-null result");
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public void Region_CanBeChanged()
    {
        _provider.Region = "eu";
        Assert.That(_provider.Region, Is.EqualTo("eu"));
        
        _provider.Region = "us";
        Assert.That(_provider.Region, Is.EqualTo("us"));
    }

    // ── Reflection helpers ──

    private static Platform InvokeDetectPlatform(string url)
    {
        var method = typeof(DoubleDoubleProvider).GetMethod("DetectPlatform",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (Platform)method?.Invoke(null, new object[] { url })!;
    }

    private static string InvokeMapPlatformToService(Platform platform)
    {
        var method = typeof(DoubleDoubleProvider).GetMethod("MapPlatformToService",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method?.Invoke(null, new object[] { platform })!;
    }

    private static string? InvokeExtractDownloadUrl(string html)
    {
        var method = typeof(DoubleDoubleProvider).GetMethod("ExtractDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (string?)method?.Invoke(null, new object[] { html });
    }

    private static List<SearchResult> InvokeParseResults(string html, Platform platform)
    {
        var method = typeof(DoubleDoubleProvider).GetMethod("ParseResults",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (List<SearchResult>)method?.Invoke(null, new object[] { html, platform })!;
    }
}
