using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class DoubleDoubleProviderTests
{
    private DoubleDoubleProvider CreateProvider()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<DoubleDoubleProvider>();
        return new DoubleDoubleProvider(logger);
    }

    [Test]
    public void ProviderMetadata_IsCorrect()
    {
        var provider = CreateProvider();
        Assert.Multiple(() =>
        {
            Assert.That(provider.Id, Is.EqualTo("doubledouble"));
            Assert.That(provider.Name, Is.EqualTo("DoubleDouble"));
            Assert.That(provider.Icon, Is.EqualTo("🔁"));
            Assert.That(provider.SupportedPlatforms, Is.EquivalentTo(new[]
            {
                Platform.AmazonMusic, Platform.Soundcloud, Platform.Qobuz, Platform.Deezer, Platform.Tidal
            }));
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        var provider = CreateProvider();
        Assert.That(provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.DoubleDouble));
    }

    [Test]
    public void DefaultRegion_IsUs()
    {
        var provider = CreateProvider();
        Assert.That(provider.Region, Is.EqualTo("us"));
    }

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
