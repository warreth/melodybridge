using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The public playlist sources against the real services: Deezer's playlist
/// API, ListenBrainz's JSPF playlists and SoundCloud sets through yt-dlp.
/// The SoundCloud set belongs to an official artist account and carries Go+
/// previews, so the test pins the safety property (a 30 second clip must
/// never become a track) rather than a track count.
/// </summary>
[TestFixture]
[Category("Live")]
public class PlaylistSourceLiveTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [Test]
    public async Task Deezer_RealPublicPlaylist_ReturnsTheFullTrackList()
    {
        var provider = new DeezerSourceProvider(Http, NullLogger<DeezerSourceProvider>.Instance);
        var playlist = await provider.GetPlaylistAsync("https://www.deezer.com/playlist/3155776842");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.Not.Empty);
            Assert.That(playlist.TrackCount, Is.GreaterThan(50),
                "the Top Worldwide playlist has 100 tracks and must not be truncated");
            Assert.That(playlist.Tracks!.All(t => !string.IsNullOrWhiteSpace(t.Title)), Is.True);
            Assert.That(playlist.Tracks!.Any(t => !string.IsNullOrWhiteSpace(t.Isrc)), Is.True,
                "the public payload carries ISRCs for most tracks");
            Assert.That(playlist.Tracks!.All(t => t.Duration is null || t.Duration.Value.TotalSeconds > 30),
                Is.True, "no preview-length entries");
        });
    }

    [Test]
    public async Task ListenBrainz_RealPublicPlaylist_ReturnsJspfTracks()
    {
        var provider = new ListenBrainzSourceProvider(Http, NullLogger<ListenBrainzSourceProvider>.Instance);
        var playlist = await provider.GetPlaylistAsync(
            "https://listenbrainz.org/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Jazz"));
            Assert.That(playlist.TrackCount, Is.GreaterThan(5));
            Assert.That(playlist.Tracks!.All(t => !string.IsNullOrWhiteSpace(t.Title)), Is.True);
            Assert.That(playlist.Tracks!.All(t => t.Duration is null || t.Duration.Value.TotalSeconds > 30),
                Is.True, "JSPF durations arrive in milliseconds and must be converted");
        });
    }

    [Test]
    public async Task SoundCloud_RealSet_NeverYieldsPreviewClips()
    {
        var provider = new SoundCloudSourceProvider(NullLogger<SoundCloudSourceProvider>.Instance);
        var playlist = await provider.GetPlaylistAsync(
            "https://soundcloud.com/daftpunkofficialmusic/sets/discovery-8");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Discovery"));
            Assert.That(playlist.Tracks, Is.Not.Null);
            Assert.That(playlist.Tracks!.All(t => t.Duration is null || t.Duration.Value.TotalSeconds > 35),
                Is.True, "a Go+ preview clip must never enter the playlist");
            if (playlist.Tracks!.Count == 0)
                Assert.That(playlist.Warning, Is.Not.Null.And.Not.Empty,
                    "skipping tracks must be reported, not silent");
        });
    }

    [Test]
    public async Task SoundCloud_RealUserSet_ReturnsFullTracks()
    {
        // A user-made set: full streams, real titles and artists. This is
        // the shape most SoundCloud sets have.
        var provider = new SoundCloudSourceProvider(NullLogger<SoundCloudSourceProvider>.Instance);
        var playlist = await provider.GetPlaylistAsync(
            "https://soundcloud.com/bruno-devegili-484857255/sets/daft-punk");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.TrackCount, Is.GreaterThan(10));
            Assert.That(playlist.Tracks!.All(t => !string.IsNullOrWhiteSpace(t.Title)), Is.True);
            Assert.That(playlist.Tracks!.All(t => !string.IsNullOrWhiteSpace(t.Artist)), Is.True);
            Assert.That(playlist.Tracks!.All(t => t.Duration is null || t.Duration.Value.TotalSeconds > 35),
                Is.True);
        });
    }
}
