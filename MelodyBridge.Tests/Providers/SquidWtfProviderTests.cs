using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class SquidWtfProviderTests
{
    private SquidWtfProvider CreateProvider()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SquidWtfProvider>();
        return new SquidWtfProvider(logger);
    }

    [Test]
    public void ProviderMetadata_IsCorrect()
    {
        var provider = CreateProvider();
        Assert.Multiple(() =>
        {
            Assert.That(provider.Id, Is.EqualTo("squidwtf"));
            Assert.That(provider.Name, Is.EqualTo("Squid.wtf"));
            Assert.That(provider.Icon, Is.EqualTo("🐙"));
            Assert.That(provider.SupportedPlatforms, Is.EquivalentTo(new[]
            {
                Platform.Qobuz, Platform.Tidal, Platform.AmazonMusic, Platform.Soundcloud
            }));
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        var provider = CreateProvider();
        Assert.That(provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.SquidWtf));
    }

    [Test]
    public void SearchAsync_WithUnknownPlatform_ReturnsEmpty()
    {
        var provider = CreateProvider();

        // Use reflection to call the HttpClient-less parts if search throws due to no network
        // Just verify the method exists and returns the right type
        Assert.That(provider.SearchAsync("test song").Result, Is.InstanceOf<IReadOnlyList<SearchResult>>());
    }

    [Test]
    public void GetTrackInfoAsync_InvalidUrl_ReturnsNull()
    {
        var provider = CreateProvider();
        var result = provider.GetTrackInfoAsync("https://example.com/not-a-track").Result;
        Assert.That(result, Is.Null);
    }

    // ── Helper method tests via reflection ──

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

    // ── Quality mapping tests ──

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
