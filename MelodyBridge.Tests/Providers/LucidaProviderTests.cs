using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class LucidaProviderTests
{
    private LucidaProvider CreateProvider()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<LucidaProvider>();
        return new LucidaProvider(logger);
    }

    [Test]
    public void ProviderMetadata_IsCorrect()
    {
        var provider = CreateProvider();
        Assert.Multiple(() =>
        {
            Assert.That(provider.Id, Is.EqualTo("lucida"));
            Assert.That(provider.Name, Is.EqualTo("Lucida.to"));
            Assert.That(provider.Icon, Is.EqualTo("🔮"));
            Assert.That(provider.SupportedPlatforms, Is.EquivalentTo(new[]
            {
                Platform.Tidal, Platform.Qobuz, Platform.Deezer,
                Platform.Soundcloud, Platform.AmazonMusic, Platform.Spotify
            }));
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        var provider = CreateProvider();
        Assert.That(provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.Lucida));
    }

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

    // MapPlatformToService tests

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
