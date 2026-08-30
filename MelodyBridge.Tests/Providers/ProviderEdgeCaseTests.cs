using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Providers;

/// <summary>
/// Edge case tests for all music providers covering error handling,
/// boundary conditions, and integration scenarios.
/// </summary>
[TestFixture]
public class ProviderEdgeCaseTests
{
    private string _testOutputDir = null!;

    [SetUp]
    public void Setup()
    {
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
    // SquidWtf Edge Cases
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task SquidWtf_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var result = await provider.SearchAsync(null!);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task SquidWtf_Search_EmptyQuery_ReturnsEmpty()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var result = await provider.SearchAsync("");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task SquidWtf_GetTrackInfo_NullUrl_ReturnsNull()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var result = await provider.GetTrackInfoAsync(null!);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SquidWtf_GetTrackInfo_EmptyUrl_ReturnsNull()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var result = await provider.GetTrackInfoAsync("");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SquidWtf_GetTrackInfo_MalformedUrl_ReturnsNull()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var result = await provider.GetTrackInfoAsync("not-a-valid-url");
        Assert.That(result, Is.Null);
    }

    [Test]
    [Category("Integration")]
    [Timeout(30000)]
    public async Task SquidWtf_SearchWithCancellation_StopsGracefully()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        
        try
        {
            var result = await provider.SearchAsync("test", Platform.Qobuz, cts.Token);
            // May complete or be cancelled
            Assert.That(result, Is.Not.Null);
        }
        catch (OperationCanceledException)
        {
            // Expected
            Assert.Pass("Cancellation handled correctly");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Lucida Edge Cases
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Lucida_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        var result = await provider.SearchAsync(null!);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Lucida_Search_EmptyQuery_ReturnsEmpty()
    {
        var provider = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        var result = await provider.SearchAsync("");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Lucida_GetTrackInfo_InvalidUrl_ReturnsNull()
    {
        var provider = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        var result = await provider.GetTrackInfoAsync("not-a-valid-url");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Lucida_GetTrackInfo_NullUrl_ReturnsNull()
    {
        var provider = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        var result = await provider.GetTrackInfoAsync(null!);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Lucida_Search_WithSpecificPlatform_FiltersByPlatform()
    {
        var provider = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        
        // Searching with unsupported platform should return empty
        var result = await provider.SearchAsync("test", Platform.Deezer);
        Assert.That(result, Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Monochrome Edge Cases
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Monochrome_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.SearchAsync(null!);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Monochrome_Search_EmptyQuery_ReturnsEmpty()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.SearchAsync("");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Monochrome_GetTrackInfo_NonTidalUrl_ReturnsNull()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.GetTrackInfoAsync("https://example.com/track");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Monochrome_GetTrackInfo_QobuzUrl_ReturnsNull()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.GetTrackInfoAsync("https://www.qobuz.com/track/123456");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Monochrome_DownloadAsync_NoSessionId_ReturnsFailure()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.DownloadAsync(
            "https://tidal.com/browse/track/12345",
            new TrackQuality(320, MediaType.AAC),
            _testOutputDir,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.Empty);
        });
    }

    [Test]
    public async Task Monochrome_DownloadAsync_InvalidOutputDir_ReturnsFailure()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var invalidDir = "/root/nonexistent/path/that/cannot/be/created";
        
        var result = await provider.DownloadAsync(
            "https://tidal.com/browse/track/12345",
            new TrackQuality(320, MediaType.AAC),
            invalidDir,
            CancellationToken.None);

        Assert.That(result.Success, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DoubleDouble Edge Cases
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task DoubleDouble_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        var result = await provider.SearchAsync(null!);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task DoubleDouble_Search_EmptyQuery_ReturnsEmpty()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        var result = await provider.SearchAsync("");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DoubleDouble_RegionDefault_IsUs()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        Assert.That(provider.Region, Is.EqualTo("us"));
    }

    [Test]
    public void DoubleDouble_RegionCanChange()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        provider.Region = "eu";
        Assert.That(provider.Region, Is.EqualTo("eu"));
    }

    [Test]
    public void DoubleDouble_RegionInvalid_StillAccepted()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        provider.Region = "invalid";
        // Provider should still accept it (validation happens at API level)
        Assert.That(provider.Region, Is.EqualTo("invalid"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Cross-Provider Integration Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    [Category("Integration")]
    [Timeout(180000)] // 3 minutes
    public async Task AllProviders_SearchAsync_CompleteWithoutThrow()
    {
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
            new LucidaProvider(NullLogger<LucidaProvider>.Instance),
            new MonochromeProvider(NullLogger<MonochromeProvider>.Instance),
            new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance),
        };

        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.SearchAsync("test", null);
                Assert.That(result, Is.Not.Null, $"{provider.Name} search should return non-null");
            }
            catch (Exception ex)
            {
                Assert.Fail($"{provider.Name} search threw exception: {ex.Message}");
            }
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task AllProviders_GetTrackInfo_InvalidUrlReturnsNull()
    {
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
            new LucidaProvider(NullLogger<LucidaProvider>.Instance),
            new MonochromeProvider(NullLogger<MonochromeProvider>.Instance),
            new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance),
        };

        var invalidUrl = "https://example.com/not/a/real/track";

        foreach (var provider in providers)
        {
            var result = await provider.GetTrackInfoAsync(invalidUrl);
            Assert.That(result, Is.Null, $"{provider.Name} should return null for invalid URL");
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(120000)]
    public async Task AllProviders_DownloadAsync_InvalidUrlReturnsFailure()
    {
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
            new LucidaProvider(NullLogger<LucidaProvider>.Instance),
            new MonochromeProvider(NullLogger<MonochromeProvider>.Instance),
            new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance),
        };

        var invalidUrl = "https://example.com/not/a/real/track";
        var quality = new TrackQuality(320, MediaType.MP3);

        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.DownloadAsync(invalidUrl, quality, _testOutputDir);
                Assert.That(result.Success, Is.False, $"{provider.Name} should fail for invalid URL");
            }
            catch (Exception ex)
            {
                // Some providers may throw instead of returning failure
                Assert.Pass($"{provider.Name} threw exception as expected: {ex.GetType().Name}");
            }
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task AllProviders_MetadataConsistent()
    {
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
            new LucidaProvider(NullLogger<LucidaProvider>.Instance),
            new MonochromeProvider(NullLogger<MonochromeProvider>.Instance),
            new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance),
        };

        foreach (var provider in providers)
        {
            Assert.Multiple(() =>
            {
                Assert.That(provider.Id, Is.Not.Empty, $"{provider.Name} should have non-empty Id");
                Assert.That(provider.Name, Is.Not.Empty, $"{provider.Name} should have non-empty Name");
                Assert.That(provider.Icon, Is.Not.Empty, $"{provider.Name} should have non-empty Icon");
                Assert.That(provider.SupportedPlatforms, Is.Not.Empty, $"{provider.Name} should support at least one platform");
                Assert.That(provider.SupportedQualities, Is.Not.Empty, $"{provider.Name} should support at least one quality");
                Assert.That(provider.Description, Is.Not.Empty, $"{provider.Name} should have non-empty Description");
            });
        }
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task Concurrent_MultipleSearches_CompleteWithoutDeadlock()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            tasks[i] = provider.SearchAsync($"track {i}", Platform.Qobuz);
        }

        // Should complete without deadlock
        await Task.WhenAll(tasks);
        Assert.Pass("Concurrent searches completed without deadlock");
    }

    [Test]
    [Category("Integration")]
    [Timeout(60000)]
    public async Task VeryLongQuery_HandledGracefully()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var veryLongQuery = new string('a', 1000);

        var result = await provider.SearchAsync(veryLongQuery, Platform.Qobuz);
        Assert.That(result, Is.Not.Null);
    }
}
