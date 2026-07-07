using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class MonochromeProviderTests
{
    private MonochromeProvider CreateProvider()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<MonochromeProvider>();
        return new MonochromeProvider(logger);
    }

    [Test]
    public void ProviderMetadata_IsCorrect()
    {
        var provider = CreateProvider();
        Assert.Multiple(() =>
        {
            Assert.That(provider.Id, Is.EqualTo("monochrome"));
            Assert.That(provider.Name, Is.EqualTo("Monochrome (TIDAL)"));
            Assert.That(provider.Icon, Is.EqualTo("🎵"));
            Assert.That(provider.SupportedPlatforms, Is.EquivalentTo(new[] { Platform.Tidal }));
        });
    }

    [Test]
    public void SupportedQualities_AreFromProviderQualities()
    {
        var provider = CreateProvider();
        Assert.That(provider.SupportedQualities, Is.EquivalentTo(ProviderQualities.Monochrome));
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
    public void TryExtractTidalTrackId_NullUrl_ThrowsArgumentNull()
    {
        Assert.That(() => InvokeTryExtractTidalTrackId(null!),
            Throws.TypeOf<TargetInvocationException>());
    }

    [Test]
    public void DownloadAsync_InvalidUrl_ReturnsFailure()
    {
        var provider = CreateProvider();
        var result = provider.DownloadAsync("https://example.com/not-tidal", new TrackQuality(320, MediaType.AAC), "/tmp").Result;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("TIDAL track ID"));
        });
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
