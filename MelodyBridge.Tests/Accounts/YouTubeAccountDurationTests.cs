using MelodyBridge.Infrastructure.Accounts;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The YouTube Data API reports durations as ISO 8601 strings; the account
/// provider parses them into the track rows so the waterfall's duration
/// gate works for account playlists too. Offline pin of the parser.
/// </summary>
[TestFixture]
public class YouTubeAccountDurationTests
{
    [TestCase("PT3M33S", 213)]
    [TestCase("PT1H2M3S", 3723)]
    [TestCase("PT45S", 45)]
    [TestCase("PT1H", 3600)]
    [TestCase("PT1M", 60)]
    public void ParseIsoDuration_RealShapes_ReturnTheTimespan(string iso, int seconds)
    {
        Assert.That(YouTubeAccountProvider.ParseIsoDuration(iso),
            Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("PT0S")]
    public void ParseIsoDuration_InvalidOrZero_ReturnsNull(string? iso)
    {
        Assert.That(YouTubeAccountProvider.ParseIsoDuration(iso), Is.Null,
            "an unknown duration is better than a wrong one: the gate matches on it");
    }
}
