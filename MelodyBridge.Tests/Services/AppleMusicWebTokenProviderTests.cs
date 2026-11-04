using MelodyBridge.Infrastructure.AppleMusic;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class AppleMusicWebTokenProviderTests
{
    [TestCase("<script src=\"/assets/index~abc.js\"></script>", "/assets/index~abc.js")]
    [TestCase("<script src=\"/assets/index-old.js\"></script>", "/assets/index-old.js")]
    public void ReadBundlePath_CurrentAndOlderNames(string html, string expected)
        => Assert.That(AppleMusicWebTokenProvider.ReadBundlePath(html), Is.EqualTo(expected));

    [Test]
    public void ReadToken_PicksTheWebPlayerIssuer()
    {
        var token = AppleMusicWebTokenProvider.ReadToken(AppleMusicTestData.Bundle);
        Assert.That(token, Is.Not.Null);
        Assert.That(AppleMusicWebTokenProvider.ReadExpiry(token!), Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public void ReadToken_NoJwt_ReturnsNull()
        => Assert.That(AppleMusicWebTokenProvider.ReadToken("const nothing = 1;"), Is.Null);

    [Test]
    public async Task GetTokenAsync_ReadsTheBundleOnceThenCaches()
    {
        var handler = new ScriptedHandler()
            .On("/assets/index", AppleMusicTestData.Bundle)
            .On("/", AppleMusicTestData.HomeHtml);
        var provider = new AppleMusicWebTokenProvider(
            new HttpClient(handler), NullLogger<AppleMusicWebTokenProvider>.Instance);

        var first = await provider.GetTokenAsync();
        var second = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(handler.Requests.Count(r => r.Url.Contains("/assets/index")), Is.EqualTo(1),
                "the 3 MB bundle is read once per token");
        });
    }

    [Test]
    public async Task Invalidate_ForcesAFreshRead()
    {
        var handler = new ScriptedHandler()
            .On("/assets/index", AppleMusicTestData.Bundle)
            .On("/", AppleMusicTestData.HomeHtml);
        var provider = new AppleMusicWebTokenProvider(
            new HttpClient(handler), NullLogger<AppleMusicWebTokenProvider>.Instance);

        await provider.GetTokenAsync();
        provider.Invalidate();
        await provider.GetTokenAsync();

        Assert.That(handler.Requests.Count(r => r.Url.Contains("/assets/index")), Is.EqualTo(2));
    }

    [Test]
    public void GetTokenAsync_NoTokenInBundle_ThrowsATokenException()
    {
        var handler = new ScriptedHandler()
            .On("/assets/index", "const nothing = 1;")
            .On("/", AppleMusicTestData.HomeHtml);
        var provider = new AppleMusicWebTokenProvider(
            new HttpClient(handler), NullLogger<AppleMusicWebTokenProvider>.Instance);

        Assert.ThrowsAsync<AppleMusicTokenException>(() => provider.GetTokenAsync());
    }
}
