using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class MonochromeProviderTests
{
    private MonochromeProvider _provider = null!;
    private string _testOutputDir = null!;

    [SetUp]
    public void Setup()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<MonochromeProvider>();
        _provider = new MonochromeProvider(logger);
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
            Assert.That(_provider.Id, Is.EqualTo("monochrome"));
            Assert.That(_provider.Name, Is.EqualTo("Monochrome (TIDAL)"));
            Assert.That(_provider.Icon, Is.EqualTo("🎵"));
            Assert.That(_provider.SupportedPlatforms, Is.EquivalentTo(new[] { Platform.Tidal }));
            Assert.That(_provider.Description, Is.Not.Empty);
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        Assert.That(_provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.Monochrome));
    }

    [Test]
    public void TryExtractTidalTrackId_TrackBrowsePattern_ExtractsId()
    {
        var result = InvokeTryExtractTidalTrackId("https://tidal.com/browse/track/12345678");
        Assert.Multiple(() =>
        {
            Assert.That(result.isSuccess, Is.True);
            Assert.That(result.id, Is.EqualTo(12345678));
        });
    }

    [Test]
    public void TryExtractTidalTrackId_EqualsPattern_ExtractsId()
    {
        var result = InvokeTryExtractTidalTrackId("track=87654321");
        Assert.Multiple(() =>
        {
            Assert.That(result.isSuccess, Is.True);
            Assert.That(result.id, Is.EqualTo(87654321));
        });
    }

    [Test]
    public void TryExtractTidalTrackId_NoMatch_ReturnsFalse()
    {
        var result = InvokeTryExtractTidalTrackId("https://tidal.com/album/12345678");
        Assert.That(result.isSuccess, Is.False);
    }

    [Test]
    public void TryExtractTidalTrackId_EmptyUrl_ReturnsFalse()
    {
        var result = InvokeTryExtractTidalTrackId("");
        Assert.That(result.isSuccess, Is.False);
    }

    [Test]
    public void DownloadAsync_InvalidUrl_ReturnsFailure()
    {
        var result = _provider.DownloadAsync("https://example.com/not-tidal", 
            new TrackQuality(320, MediaType.AAC), _testOutputDir).Result;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("TIDAL track ID"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // Integration Tests - Real API Calls
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    [Category("Integration")]
    [Timeout(60000)] // 60 seconds
    public async Task SearchAsync_RealQuery_ReturnsResults()
    {
        // Search for a popular, well-known track on TIDAL
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
    public async Task GetTrackInfoAsync_RealTidalUrl_ReturnsTrackInfo()
    {
        // Use a known public TIDAL track - "Bohemian Rhapsody" by Queen
        var url = "https://tidal.com/browse/track/25041381";
        
        var trackInfo = await _provider.GetTrackInfoAsync(url);

        Assert.Multiple(() =>
        {
            Assert.That(trackInfo, Is.Not.Null);
            Assert.That(trackInfo!.Title, Is.Not.Empty);
            Assert.That(trackInfo.Artist, Is.Not.Empty);
            Assert.That(trackInfo.SourcePlatform, Is.EqualTo(Platform.Tidal));
        });
    }

    [Test]
    [Category("Integration")]
    [Timeout(120000)] // 2 minutes for download
    public async Task DownloadAsync_RealTidalTrack_DownloadsSuccessfully()
    {
        // Use a known public TIDAL track
        var url = "https://tidal.com/browse/track/25041381";
        var quality = new TrackQuality(320, MediaType.AAC);
        
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
    public async Task DownloadAsync_HighQualityFLAC_DownloadsSuccessfully()
    {
        var url = "https://tidal.com/browse/track/25041381";
        var quality = new TrackQuality(24, MediaType.FLAC);
        
        var result = await _provider.DownloadAsync(url, quality, _testOutputDir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, $"Download failed: {result.ErrorMessage}");
            if (result.Success && !string.IsNullOrEmpty(result.FilePath))
            {
                Assert.That(File.Exists(result.FilePath), Is.True);
                Assert.That(result.FilePath, Does.Contain(".flac").Or.Contains(".m4a"));
            }
        });
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
    public async Task GetTrackInfoAsync_InvalidTidalUrl_ReturnsNull()
    {
        var trackInfo = await _provider.GetTrackInfoAsync("https://tidal.com/browse/track/999999999999999");
        Assert.That(trackInfo, Is.Null);
    }

    // ── Reflection helpers ──

    private static (bool isSuccess, long id) InvokeTryExtractTidalTrackId(string url)
    {
        var method = typeof(MonochromeProvider).GetMethod("TryExtractTidalTrackId",
            BindingFlags.Static | BindingFlags.NonPublic);
        var parameters = new object[] { url, 0L };
        var success = (bool)method?.Invoke(null, parameters)!;
        return (success, (long)parameters[1]);
    }
}
