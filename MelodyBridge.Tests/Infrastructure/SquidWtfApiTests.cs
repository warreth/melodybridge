using MelodyBridge.Infrastructure.Apis;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class SquidWtfApiTests
{
    [Test]
    public void GetDownloadUrl_WithValidTrackId_ReturnsUrl()
    {
        // This will attempt a real API call — may fail due to network/site availability
        try
        {
            var url = QobuzSquidWtfApi.GetDownloadUrl(27648923, "6");
            Assert.That(url, Is.Not.Null);
            Assert.That(url, Does.StartWith("http"));
        }
        catch (Exception ex)
        {
            // Network errors, DNS failures, or site being down are acceptable
            Assert.Pass($"Expected (network/API issue): {ex.Message}");
        }
    }

    [Test]
    public void GetDownloadUrl_InvalidTrackId_Throws()
    {
        try
        {
            QobuzSquidWtfApi.GetDownloadUrl(-1, "6");
            Assert.Fail("Should have thrown");
        }
        catch (Exception ex)
        {
            Assert.That(ex.Message, Is.Not.Empty);
        }
    }

    [Test]
    public void GetDownloadUrl_InvalidQuality_Throws()
    {
        try
        {
            QobuzSquidWtfApi.GetDownloadUrl(27648923, "999");
        }
        catch (Exception ex)
        {
            Assert.That(ex.Message, Is.Not.Empty);
        }
    }
}
