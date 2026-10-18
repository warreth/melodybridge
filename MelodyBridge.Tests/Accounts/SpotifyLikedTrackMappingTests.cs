using MelodyBridge.Infrastructure.Accounts;
using SpotifyAPI.Web;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// Liked songs come back as full Spotify track objects. The album object
/// on them carries the album tag, the release credit and the cover art;
/// dropping it left every liked-songs download without album metadata,
/// which made media servers group the whole playlist into one folder
/// album with one image.
/// </summary>
[TestFixture]
public class SpotifyLikedTrackMappingTests
{
    [Test]
    public void MapSavedTrack_CarriesAlbumCreditAndTheLargestCover()
    {
        var track = new FullTrack
        {
            Id = "4uLU6hMCjMI75M1A2tKUQC",
            Name = "Die With A Smile",
            DurationMs = 252000,
            Artists = new List<SimpleArtist>
            {
                new() { Name = "Lady Gaga" },
                new() { Name = "Bruno Mars" },
            },
            Album = new SimpleAlbum
            {
                Name = "MAYHEM",
                Artists = new List<SimpleArtist> { new() { Name = "Lady Gaga" } },
                Images = new List<Image>
                {
                    new() { Url = "https://i.scdn.co/image/small", Width = 64, Height = 64 },
                    new() { Url = "https://i.scdn.co/image/big", Width = 640, Height = 640 },
                },
            },
        };

        var mapped = SpotifyAccountProvider.MapSavedTrack(track);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Title, Is.EqualTo("Die With A Smile"));
            Assert.That(mapped.Artist, Is.EqualTo("Lady Gaga, Bruno Mars"));
            Assert.That(mapped.Album, Is.EqualTo("MAYHEM"),
                "the album tag is what a media server groups on");
            Assert.That(mapped.AlbumArtist, Is.EqualTo("Lady Gaga"),
                "the release credit, not the track's featured artists");
            Assert.That(mapped.CoverUrl, Is.EqualTo("https://i.scdn.co/image/big"),
                "the largest image is the one worth embedding");
            Assert.That(mapped.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(252000)));
            Assert.That(mapped.IsLiked, Is.True);
            Assert.That(mapped.Isrc, Is.Null,
                "the separate /v1/tracks batch fills the ISRC after this mapping");
        });
    }

    [Test]
    public void MapSavedTrack_WithoutAnAlbum_LeavesTheFieldsEmpty()
    {
        var mapped = SpotifyAccountProvider.MapSavedTrack(new FullTrack
        {
            Id = "local-1",
            Name = "Local Upload",
            DurationMs = 120000,
        });

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Album, Is.Null);
            Assert.That(mapped.AlbumArtist, Is.Null);
            Assert.That(mapped.CoverUrl, Is.Null);
            Assert.That(mapped.Title, Is.EqualTo("Local Upload"));
        });
    }
}
