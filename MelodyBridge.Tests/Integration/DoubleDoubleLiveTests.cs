using System.Net;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// DoubleDouble against the real site: the availability verdict must agree
/// with what an actual submission answers. The site turned captcha-gated,
/// which is exactly why the plugin reports itself unavailable; if the gate
/// ever drops, this test flips and the plugin goes ready again.
/// </summary>
[TestFixture]
[Category("Live")]
public class DoubleDoubleLiveTests
{
    [Test]
    public async Task Availability_AgreesWithARealSubmission()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // One real submission probe: the site either takes automated rips
        // or it demands a captcha, and that answer is the ground truth.
        var probeUrl = "https://us.doubledouble.top/dl?url="
            + Uri.EscapeDataString("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT");
        using var probe = await http.GetAsync(probeUrl);
        var body = await probe.Content.ReadAsStringAsync();

        var captchaGated = probe.StatusCode == HttpStatusCode.Forbidden
            || body.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase);

        var downloader = new DoubleDoubleDownloader(http, NullLogger<DoubleDoubleDownloader>.Instance);
        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.EqualTo(!captchaGated),
                $"a real submission said captcha={captchaGated}, so availability must be {!captchaGated}");
            if (!available)
                Assert.That(downloader.LastUnavailableReason, Does.Contain("captcha"),
                    "an unavailable verdict must name the captcha, not a generic failure");
        });
    }
}
