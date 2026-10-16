using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live ISRC enrichment against Deezer's public API: real lookups for
/// well-known and obscure tracks, and the honest null when nothing
/// matches confidently.
/// </summary>
[TestFixture]
[Category("Live")]
public class IsrcResolverLiveTests
{
    private IsrcResolver _resolver = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (github.com/warreth/melodybridge)");
        _resolver = new IsrcResolver(http, NullLogger<IsrcResolver>.Instance);
    }

    private static bool LooksLikeAnIsrc(string? isrc)
        => isrc is { Length: 12 } && isrc.All(char.IsLetterOrDigit);

    [Test]
    public async Task Resolve_WellKnownTrack_ReturnsARealIsrc()
    {
        var isrc = await _resolver.ResolveAsync(
            "Rick Astley", "Never Gonna Give You Up", TimeSpan.FromSeconds(213));

        Assert.That(LooksLikeAnIsrc(isrc), Is.True, $"got: {isrc ?? "null"}");
    }

    [Test]
    public async Task Resolve_ObscureTrack_StillFindsOne()
    {
        var isrc = await _resolver.ResolveAsync("Forss", "Flickermood", TimeSpan.FromSeconds(214));

        Assert.That(LooksLikeAnIsrc(isrc), Is.True, $"got: {isrc ?? "null"}");
    }

    [Test]
    public async Task Resolve_Nonsense_ReturnsNullWithoutThrowing()
    {
        var isrc = await _resolver.ResolveAsync(
            "zzzxqj", "qqqwwwzzz nonexistent track 999", TimeSpan.FromSeconds(180));

        Assert.That(isrc, Is.Null, "no confident match must not invent a code");
    }

    [Test]
    public async Task Resolve_WithDeezerUnreachable_FallsBackToMusicBrainz()
    {
        // Deezer cannot answer, so the MusicBrainz fallback must resolve
        // the recording on its own: the ISRC of this track is in
        // MusicBrainz (live-verified).
        var deezerDown = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:9"),
            Timeout = TimeSpan.FromSeconds(2),
        };
        var musicBrainz = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        musicBrainz.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (github.com/warreth/melodybridge)");
        var resolver = new IsrcResolver(deezerDown, NullLogger<IsrcResolver>.Instance, musicBrainz);

        var isrc = await resolver.ResolveAsync("Forss", "Flickermood", TimeSpan.FromSeconds(214));

        Assert.That(LooksLikeAnIsrc(isrc), Is.True,
            $"the MusicBrainz fallback must resolve, got: {isrc ?? "null"}");
    }
}
