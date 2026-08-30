using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class SquidWtfProviderTests
{
    private SquidWtfProvider _provider = null!;
    private string _testOutputDir = null!;

    [SetUp]
    public void Setup()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SquidWtfProvider>();
        _provider = new SquidWtfProvider(logger);
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
    // Unit Tests - Metadata & Helper Methods
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void ProviderMetadata_IsCorrect()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_provider.Id, Is.EqualTo("squidwtf"));
            Assert.That(_provider.Name, Is.EqualTo("Squid.wtf"));
            Assert.That(_provider.Icon, Is.EqualTo("🐙"));
            Assert.That(_provider.SupportedPlatforms, Is.EquivalentTo(new[]
            {
                Platform.Qobuz, Platform.Tidal, Platform.AmazonMusic, Platform.Soundcloud
            }));
            Assert.That(_provider.Description, Is.Not.Empty);
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        Assert.That(_provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.SquidWtf));
    }

    // ── Helper method tests via reflection ──

    [Test]
    public void GetTrackInfoAsync_InvalidUrl_ReturnsNull()
    {
        var result = _provider.GetTrackInfoAsync("https://example.com/not-a-track").Result;
        Assert.That(result, Is.Null);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Platform Detection
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void DetectPlatform_QobuzUrl_ReturnsQobuz()
    {
        var result = InvokeDetectPlatform("https://www.qobuz.com/track/123456");
        Assert.That(result, Is.EqualTo(Platform.Qobuz));
    }

    [Test]
    public void DetectPlatform_TidalUrl_ReturnsTidal()
    {
        var result = InvokeDetectPlatform("https://tidal.com/browse/track/123456");
        Assert.That(result, Is.EqualTo(Platform.Tidal));
    }

    [Test]
    public void DetectPlatform_AmazonUrl_ReturnsAmazonMusic()
    {
        var result = InvokeDetectPlatform("https://music.amazon.com/track/abc123");
        Assert.That(result, Is.EqualTo(Platform.AmazonMusic));
    }

    [Test]
    public void DetectPlatform_SoundcloudUrl_ReturnsSoundcloud()
    {
        var result = InvokeDetectPlatform("https://soundcloud.com/artist/track");
        Assert.That(result, Is.EqualTo(Platform.Soundcloud));
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
    // Unit Tests - ID Extraction
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void TryExtractQobuzTrackId_TrackPattern_ExtractsId()
    {
        var result = InvokeTryExtractQobuzTrackId("https://www.qobuz.com/track/27648923");
        Assert.That(result.isSuccess, Is.True);
        Assert.That(result.id, Is.EqualTo(27648923));
    }

    [Test]
    public void TryExtractQobuzTrackId_TrackIdParam_ExtractsId()
    {
        var result = InvokeTryExtractQobuzTrackId("https://www.qobuz.com/api.json/0.2/track/get?track_id=27648923");
        Assert.That(result.isSuccess, Is.True);
        Assert.That(result.id, Is.EqualTo(27648923));
    }

    [Test]
    public void TryExtractQobuzTrackId_NoMatch_ReturnsFalse()
    {
        var result = InvokeTryExtractQobuzTrackId("https://www.qobuz.com/album/123456");
        Assert.That(result.isSuccess, Is.False);
    }

    [Test]
    public void TryExtractTidalTrackId_TrackPattern_ExtractsId()
    {
        var result = InvokeTryExtractTidalTrackId("https://tidal.com/browse/track/98765432");
        Assert.That(result.isSuccess, Is.True);
        Assert.That(result.id, Is.EqualTo(98765432));
    }

    [Test]
    public void TryExtractTidalTrackId_EqualsPattern_ExtractsId()
    {
        var result = InvokeTryExtractTidalTrackId("https://api.tidal.com/v1/tracks?track=555555");
        Assert.That(result.isSuccess, Is.True);
        Assert.That(result.id, Is.EqualTo(555555));
    }

    [Test]
    public void TryExtractTidalTrackId_NoMatch_ReturnsFalse()
    {
        var result = InvokeTryExtractTidalTrackId("https://tidal.com/album/123456");
        Assert.That(result.isSuccess, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Unit Tests - Quality Mapping
    // ═══════════════════════════════════════════════════════════════════

    [TestCase(24, MediaType.FLAC, "27")]
    [TestCase(320, MediaType.MP3, "6")]
    [TestCase(320, MediaType.AAC, "4")]
    [TestCase(320, MediaType.OPUS, "5")]
    [TestCase(16, MediaType.FLAC, "6")]
    public void MapQualityToCode_ReturnsExpectedCode(int bitrate, MediaType format, string expectedCode)
    {
        var quality = new TrackQuality(bitrate, format);
        var code = InvokeMapQualityToCode(quality);
        Assert.That(code, Is.EqualTo(expectedCode));
    }

    [Test]
    public void MapQualityToCode_UnknownQuality_Returns6()
    {
        var quality = new TrackQuality(128, MediaType.MP3);
        var code = InvokeMapQualityToCode(quality);
        Assert.That(code, Is.EqualTo("6"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Integration Tests - Real API Calls (Qobuz)
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_RealQobuzQuery_ReturnsResults()
    {
        // Search for a popular track on Qobuz
        var results = await _provider.SearchAsync("Bohemian Rhapsody Queen", Platform.Qobuz);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Null);
            Assert.That(results.Count, Is.GreaterThan(0), "Should return at least one search result");
            
            var firstResult = results[0];
            Assert.That(firstResult.Title, Is.Not.Empty);
            Assert.That(firstResult.Artist, Is.Not.Empty);
            Assert.That(firstResult.Url, Is.Not.Empty);
            Assert.That(firstResult.SourcePlatform, Is.EqualTo(Platform.Qobuz));
            Assert.That(firstResult.AvailableQualities, Is.Not.Empty);
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_QobuzPublicApi_FindsSpecificTrack()
    {
        // Search for Pink Floyd - The Dark Side of the Moon
        var results = await _provider.SearchAsync("Pink Floyd Dark Side Moon", Platform.Qobuz);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Not.Null);
            Assert.That(results.Count, Is.GreaterThan(0));
            
            // Verify we got results with proper metadata
            foreach (var result in results.Take(5))
            {
                Assert.That(result.Title, Is.Not.Empty);
                Assert.That(result.Url, Does.Contain("qobuz.com"));
            }
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_RealQobuzUrl_ReturnsTrackInfo()
    {
        // Use a known Qobuz track - popular classical or jazz track
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
    [Timeout(120000)]
    public async Task DownloadAsync_RealQobuzTrack_DownloadsSuccessfully()
    {
        // Use a known Qobuz track
        var url = "https://www.qobuz.com/track/27648923";
        var quality = new TrackQuality(320, MediaType.MP3);
        
        var result = await _provider.DownloadAsync(url, quality, _testOutputDir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, $"Download failed: {result.ErrorMessage}");
            Assert.That(result.FilePath, Is.Not.Empty);
            Assert.That(File.Exists(result.FilePath), Is.True, "Downloaded file should exist");
            
            var fileInfo = new FileInfo(result.FilePath);
            Assert.That(fileInfo.Length, Is.GreaterThan(1000), "Downloaded file should have content");
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(120000)]
    public async Task DownloadAsync_HighQualityFLAC_Qobuz_DownloadsSuccessfully()
    {
        var url = "https://www.qobuz.com/track/27648923";
        var quality = new TrackQuality(24, MediaType.FLAC);
        
        var result = await _provider.DownloadAsync(url, quality, _testOutputDir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, $"Download failed: {result.ErrorMessage}");
            if (result.Success && !string.IsNullOrEmpty(result.FilePath))
            {
                Assert.That(File.Exists(result.FilePath), Is.True);
                Assert.That(result.FilePath, Does.Contain(".flac").Or.Contains(".mp3"));
            }
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task SearchAsync_NonQobuzPlatform_ReturnsEmpty()
    {
        // SquidWtf only supports Qobuz search
        var results = await _provider.SearchAsync("test song", Platform.Tidal);
        Assert.That(results, Is.Empty);
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task GetTrackInfoAsync_InvalidQobuzUrl_ReturnsNull()
    {
        var trackInfo = await _provider.GetTrackInfoAsync("https://www.qobuz.com/track/999999999999999");
        Assert.That(trackInfo, Is.Null);
    }

    // ── Reflection helpers ──

    private static Platform InvokeDetectPlatform(string url)
    {
        var method = typeof(SquidWtfProvider).GetMethod("DetectPlatform",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (Platform)method?.Invoke(null, new object[] { url })!;
    }

    private static (bool isSuccess, long id) InvokeTryExtractQobuzTrackId(string url)
    {
        var method = typeof(SquidWtfProvider).GetMethod("TryExtractQobuzTrackId",
            BindingFlags.Static | BindingFlags.NonPublic);
        var parameters = new object[] { url, 0L };
        var success = (bool)method?.Invoke(null, parameters)!;
        return (success, (long)parameters[1]);
    }

    private static (bool isSuccess, long id) InvokeTryExtractTidalTrackId(string url)
    {
        var method = typeof(SquidWtfProvider).GetMethod("TryExtractTidalTrackId",
            BindingFlags.Static | BindingFlags.NonPublic);
        var parameters = new object[] { url, 0L };
        var success = (bool)method?.Invoke(null, parameters)!;
        return (success, (long)parameters[1]);
    }

    private static string InvokeMapQualityToCode(TrackQuality quality)
    {
        var method = typeof(SquidWtfProvider).GetMethod("MapQualityToCode",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method?.Invoke(null, new object[] { quality })!;
    }
}
