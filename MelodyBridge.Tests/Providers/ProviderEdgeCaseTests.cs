using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Providers;

[TestFixture]
public class ProviderEdgeCaseTests
{
    [Test]
    public async Task SquidWtf_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        var result = await provider.SearchAsync(null!);
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
    public async Task Lucida_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        var result = await provider.SearchAsync(null!);
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
    public async Task Monochrome_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.SearchAsync(null!);
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
    public async Task Monochrome_DownloadAsync_NoSessionId_ReturnsFailure()
    {
        var provider = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
        var result = await provider.DownloadAsync(
            "https://tidal.com/browse/track/12345",
            new TrackQuality(320, MediaType.AAC),
            "/tmp/output",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("authentication"));
        });
    }

    [Test]
    public async Task DoubleDouble_Search_NullQuery_ReturnsEmpty()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        var result = await provider.SearchAsync(null!);
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
}
