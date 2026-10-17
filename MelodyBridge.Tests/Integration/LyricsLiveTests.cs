using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The lyrics resolver against the real LRCLIB: a well-known track has
/// lyrics, a nonsense track does not, and neither answer throws. Skipped
/// when the service cannot be reached at all.
/// </summary>
[TestFixture]
[Category("Live")]
public class LyricsLiveTests
{
    private static LyricsResolver Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
        return new LyricsResolver(http, NullLogger<LyricsResolver>.Instance);
    }

    [Test]
    public async Task Resolve_WellKnownTrack_ReturnsPlainLyrics()
    {
        var resolver = Create();
        var lyrics = await resolver.ResolveAsync(
            "Daft Punk", "Around the World", "Homework", TimeSpan.FromSeconds(429));

        // The free service answers 503 when it is busy; one retry rides
        // out a momentary overload instead of skipping the proof.
        if (lyrics is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            lyrics = await resolver.ResolveAsync(
                "Daft Punk", "Around the World", "Homework", TimeSpan.FromSeconds(430));
        }

        if (lyrics is null)
            Assert.Ignore("LRCLIB did not answer (offline or rate limited)");

        Assert.That(lyrics, Does.Contain("Around the world"),
            "the resolved text must be the real lyrics, not a guess");
    }

    [Test]
    public async Task Resolve_NonsenseTrack_ReturnsNull()
    {
        var lyrics = await Create().ResolveAsync(
            "zzzzqj", "qqqwwwzzz nonexistent track 999", duration: TimeSpan.FromSeconds(7));

        Assert.That(lyrics, Is.Null, "no lyrics must come back as null, not an exception");
    }
}
