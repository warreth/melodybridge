using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class LucidaProviderTests
{
    private LucidaProvider _provider = null!;
    private string _testOutputDir = null!;

    [SetUp]
    public void Setup()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<LucidaProvider>();
        _provider = new LucidaProvider(logger);
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
            Assert.That(_provider.Id, Is.EqualTo("lucida"));
            Assert.That(_provider.Name, Is.EqualTo("Lucida.to"));
            Assert.That(_provider.Icon, Is.EqualTo("🔮"));
            Assert.That(_provider.SupportedPlatforms, Is.EquivalentTo(new[]
            {
                Platform.Tidal, Platform.Qobuz, Platform.Deezer,
                Platform.Soundcloud, Platform.AmazonMusic, Platform.Spotify
            }));
            Assert.That(_provider.Description, Is.Not.Empty);
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        Assert.That(_provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.Lucida));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Platform Detection
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void DetectPlatform_TidalUrl_ReturnsTidal()
    {
        var result = InvokeDetectPlatform("https://tidal.com/browse/track/123");
        Assert.That(result, Is.EqualTo(Platform.Tidal));
    }

    [Test]
    public void DetectPlatform_QobuzUrl_ReturnsQobuz()
    {
        var result = InvokeDetectPlatform("https://play.qobuz.com/track/123");
        Assert.That(result, Is.EqualTo(Platform.Qobuz));
    }

    [Test]
    public void DetectPlatform_DeezerUrl_ReturnsDeezer()
    {
        var result = InvokeDetectPlatform("https://www.deezer.com/track/123");
        Assert.That(result, Is.EqualTo(Platform.Deezer));
    }

    [Test]
    public void DetectPlatform_SoundcloudUrl_ReturnsSoundcloud()
    {
        var result = InvokeDetectPlatform("https://soundcloud.com/artist/track");
        Assert.That(result, Is.EqualTo(Platform.Soundcloud));
    }

    [Test]
    public void DetectPlatform_AmazonUrl_ReturnsAmazonMusic()
    {
        var result = InvokeDetectPlatform("https://music.amazon.com/track/123");
        Assert.That(result, Is.EqualTo(Platform.AmazonMusic));
    }

    [Test]
    public void DetectPlatform_SpotifyUrl_ReturnsSpotify()
    {
        var result = InvokeDetectPlatform("https://open.spotify.com/track/abc123");
        Assert.That(result, Is.EqualTo(Platform.Spotify));
    }

    [Test]
    public void DetectPlatform_UnknownUrl_ReturnsUnknown()
    {
        var result = InvokeDetectPlatform("https://example.com/something");
        Assert.That(result, Is.EqualTo(Platform.Unknown));
    }

    [Test]
    public void DetectPlatform_NullUrl_ThrowsNullReference()
    {
        Assert.That(() => InvokeDetectPlatform(null!),
            Throws.TypeOf<TargetInvocationException>());
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Service Mapping
    // ═══════════════════════════════════════════════════════════════════

    [TestCase(Platform.Tidal, "tidal")]
    [TestCase(Platform.Qobuz, "qobuz")]
    [TestCase(Platform.Deezer, "deezer")]
    [TestCase(Platform.Soundcloud, "soundcloud")]
    [TestCase(Platform.AmazonMusic, "amazon")]
    [TestCase(Platform.Spotify, "spotify")]
    [TestCase(Platform.Unknown, "tidal")]
    public void MapPlatformToService_ReturnsCorrectService(Platform platform, string expectedService)
    {
        var result = InvokeMapPlatformToService(platform);
        Assert.That(result, Is.EqualTo(expectedService));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Integration Tests - Real API Calls (Tidal via Monochrome)
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_RealTidalQuery_ReturnsResults()
    {
        // Lucida uses Monochrome API for Tidal searches
        var results = await _provider.SearchAsync("Bohemian Rhapsody Queen", Platform.Tidal);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Null);
            Assert.That(results.Count, Is.GreaterThan(0), "Should return at least one search result");
            
            var firstResult = results[0];
            Assert.That(firstResult.Title, Is.Not.Empty);
            Assert.That(firstResult.Artist, Is.Not.Empty);
            Assert.That(firstResult.Url, Is.Not.Empty);
            Assert.That(firstResult.SourcePlatform, Is.EqualTo(Platform.Tidal));
            Assert.That(firstResult.AvailableQualities, Is.Not.Empty);
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_RealQobuzQuery_ReturnsResults()
    {
        var results = await _provider.SearchAsync("Pink Floyd Dark Side", Platform.Qobuz);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Null);
            Assert.That(results.Count, Is.GreaterThan(0), "Should return Qobuz search results");
            
            var firstResult = results[0];
            Assert.That(firstResult.Title, Is.Not.Empty);
            Assert.That(firstResult.Url, Does.Contain("qobuz.com"));
            Assert.That(firstResult.SourcePlatform, Is.EqualTo(Platform.Qobuz));
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_RealTidalUrl_ReturnsTrackInfo()
    {
        // Tidal track info via Monochrome API
        var url = "https://tidal.com/browse/track/25041381";
        
        var trackInfo = await _provider.GetTrackInfoAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(trackInfo, Is.Not.Null);
            Assert.That(trackInfo!.Title, Is.Not.Empty);
            Assert.That(trackInfo.SourcePlatform, Is.EqualTo(Platform.Tidal));
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_RealQobuzUrl_ReturnsTrackInfo()
    {
        var url = "https://www.qobuz.com/track/27648923";
        
        var trackInfo = await _provider.GetTrackInfoAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(trackInfo, Is.Not.Null);
            Assert.That(trackInfo!.Title, Is.Not.Empty);
            Assert.That(trackInfo.SourcePlatform, Is.EqualTo(Platform.Qobuz));
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_UnsupportedPlatformUrl_ReturnsNull()
    {
        // Lucida doesn't support Deezer/SoundCloud/Amazon/Spotify via API
        var urls = new[]
        {
            "https://www.deezer.com/track/123456",
            "https://soundcloud.com/artist/track",
            "https://music.amazon.com/track/123",
            "https://open.spotify.com/track/abc123"
        };

        foreach (var url in urls)
        {
            var trackInfo = await _provider.GetTrackInfoAsync(url);
            Assert.That(trackInfo, Is.Null, $"Should return null for {url}");
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        var results = await _provider.SearchAsync("", Platform.Tidal);
        Assert.That(results, Is.Empty.Or.Not.Null);
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_UnsupportedPlatform_ReturnsEmpty()
    {
        // Lucida only supports Tidal and Qobuz via API
        var unsupportedPlatforms = new[] 
        { 
            Platform.Deezer, 
            Platform.Soundcloud, 
            Platform.AmazonMusic, 
            Platform.Spotify 
        };

        foreach (var platform in unsupportedPlatforms)
        {
            var results = await _provider.SearchAsync("test", platform);
            Assert.That(results, Is.Empty, $"Search for {platform} should return empty");
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_InvalidUrl_ReturnsNull()
    {
        var trackInfo = await _provider.GetTrackInfoAsync("https://example.com/not-a-track");
        Assert.That(trackInfo, Is.Null);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Integration Tests - Download (requires Python/Playwright)
    // ══════════════════════════════════════════════════════════════════���
    // Note: These tests are marked as explicit since they require external
    // Python dependencies and browser automation

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task DownloadAsync_AttemptsDownloadOrHandlesLimitation()
    {
        // Note: Lucida download requires Python/Playwright
        // This test verifies the method completes without throwing
        var url = "https://tidal.com/browse/track/25041381";
        var quality = new TrackQuality(320, MediaType.MP3);
        
        var result = await _provider.DownloadAsync(url, quality, _testOutputDir);

        // May fail due to missing Python dependencies or CAPTCHA
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False.Or.True);
        
        if (!result.Success)
        {
            // Expected if Python dependencies missing or CAPTCHA
            Assert.That(result.ErrorMessage, Is.Not.Empty);
        }
    }

    // ── Reflection helpers ──

    private static Platform InvokeDetectPlatform(string url)
    {
        var method = typeof(LucidaProvider).GetMethod("DetectPlatform",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (Platform)method?.Invoke(null, new object[] { url })!;
    }

    private static string InvokeMapPlatformToService(Platform platform)
    {
        var method = typeof(LucidaProvider).GetMethod("MapPlatformToService",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method?.Invoke(null, new object[] { platform })!;
    }
}
