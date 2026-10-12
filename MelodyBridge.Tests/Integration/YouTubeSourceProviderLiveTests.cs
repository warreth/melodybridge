using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live YouTube playlist tests via yt-dlp: a long reference playlist must
/// come back complete (not truncated at the first page), with durations
/// and artists.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("YouTube")]
public class YouTubeSourceProviderLiveTests
{
    private const string PlaylistUrl =
        "https://music.youtube.com/playlist?list=PLt4q-PB58aEoYoJCuMTAqtw4F_HvkEBvd";

    private YouTubeSourceProvider _provider = null!;

    [OneTimeSetUp]
    public void Setup()
        => _provider = new YouTubeSourceProvider(NullLogger<YouTubeSourceProvider>.Instance);

    [Test]
    public async Task GetPlaylistAsync_LongPlaylist_ReturnsAllTracks()
    {
        var playlist = await _provider.GetPlaylistAsync(PlaylistUrl);

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("POP Remix"), "playlist title must come through");
            Assert.That(playlist.Owner, Is.Not.Null.And.Not.Empty, "channel name must map to Owner");
            // The playlist holds around 145 tracks today; anything over a
            // hundred proves the flat fetch does not stop at one page.
            Assert.That(playlist.TrackCount, Is.GreaterThan(100),
                "the full playlist must be fetched, not truncated at the first page");
            Assert.That(playlist.Tracks, Has.Count.EqualTo(playlist.TrackCount));
        });
    }

    [Test]
    public async Task GetPlaylistAsync_TracksHaveDurationsAndArtists()
    {
        var playlist = await _provider.GetPlaylistAsync(PlaylistUrl);

        var withDuration = playlist.Tracks!.Count(t => t.Duration > TimeSpan.Zero);
        Assert.That(withDuration, Is.GreaterThan(100),
            "most entries must carry a duration for the UI and matching");

        var withArtist = playlist.Tracks!.Count(t => !string.IsNullOrWhiteSpace(t.Artist));
        Assert.That(withArtist, Is.GreaterThan(100), "uploader must map to Artist");
    }

    [Test]
    public async Task GetPlaylistAsync_ReturnsCoverImageUrl()
    {
        var playlist = await _provider.GetPlaylistAsync(PlaylistUrl);

        Assert.That(playlist.CoverImageUrl, Is.Not.Null.And.Not.Empty,
            "yt-dlp exposes playlist thumbnails; the tallest one must map to CoverImageUrl");
        Assert.That(playlist.CoverImageUrl, Does.StartWith("http"),
            "the cover must be a usable image URL");
    }

    [Test]
    public void GetPlaylistId_ExtractsListParameter()
    {
        var id = YouTubeSourceProvider.GetPlaylistId(
            "https://music.youtube.com/playlist?list=PLt4q-PB58aEoYoJCuMTAqtw4F_HvkEBvd&si=abc");
        Assert.That(id, Is.EqualTo("PLt4q-PB58aEoYoJCuMTAqtw4F_HvkEBvd"));
    }

    [Test]
    public void CanHandle_MatchesYouTubeUrlsAndIds()
    {
        Assert.That(_provider.CanHandle(PlaylistUrl), Is.True);
        Assert.That(_provider.CanHandle("https://www.youtube.com/playlist?list=PLxyz"), Is.True);
        Assert.That(_provider.CanHandle("PLxyz123456"), Is.True);
        Assert.That(_provider.CanHandle("https://open.spotify.com/playlist/abc"), Is.False);
    }
}
