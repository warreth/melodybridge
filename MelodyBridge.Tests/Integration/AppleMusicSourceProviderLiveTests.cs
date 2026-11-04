using MelodyBridge.Infrastructure.AppleMusic;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live Apple Music probes: one editorial playlist and one user-shared
/// playlist, both without an account. The ISRC assertion proves the API
/// path ran; the page fallback has no ISRC by design.
/// </summary>
[TestFixture]
[Category("Live")]
public class AppleMusicSourceProviderLiveTests
{
    private AppleMusicSourceProvider NewProvider()
    {
        var tokenProvider = new AppleMusicWebTokenProvider(
            new HttpClient { Timeout = TimeSpan.FromSeconds(90) },
            NullLogger<AppleMusicWebTokenProvider>.Instance);
        var api = new AppleMusicApiClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            tokenProvider,
            NullLogger<AppleMusicApiClient>.Instance);
        return new AppleMusicSourceProvider(
            api,
            NullLogger<AppleMusicSourceProvider>.Instance,
            new HttpClient { Timeout = TimeSpan.FromSeconds(40) });
    }

    [Test]
    public async Task EditorialPlaylist_FetchesTracksAndIsrc()
    {
        var playlist = await NewProvider().GetPlaylistAsync(
            "https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.Not.Empty);
            Assert.That(playlist.Tracks, Has.Count.GreaterThan(40));
            Assert.That(playlist.Tracks![0].Title, Is.Not.Empty);
            Assert.That(playlist.Tracks[0].Artist, Is.Not.Empty);
            Assert.That(playlist.Tracks[0].Duration, Is.Not.Null);
            Assert.That(playlist.Tracks[0].Isrc, Is.Not.Null.And.Not.Empty,
                "the API path carries the ISRC");
        });
    }

    [Test]
    public async Task SharedUserPlaylist_FetchesTracksWithoutAnAccount()
    {
        var playlist = await NewProvider().GetPlaylistAsync(
            "https://music.apple.com/us/playlist/aye-mane/pl.u-vxy6693I1l0Nko");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Aye Mane!").IgnoreCase);
            Assert.That(playlist.Tracks, Has.Count.GreaterThan(90));
            Assert.That(playlist.Tracks![0].Isrc, Is.Not.Null.And.Not.Empty);
        });
    }
}
