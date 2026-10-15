using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Pure parse tests for the yt-dlp thumbnail picker: highest "height" in
/// the root thumbnails array wins, then the root "thumbnail" string, then
/// the first entry's thumbnail. No network involved.
/// </summary>
[TestFixture]
public class YouTubeSourceProviderTests
{
    private static JsonElement Root(string json)
        => JsonDocument.Parse(json).RootElement;

    [Test]
    public void PickThumbnail_PrefersTallestEntry_InThumbnailsArray()
    {
        var root = Root("""
            {"title": "p",
             "thumbnails": [
               {"url": "https://i.example/120.jpg", "height": 120},
               {"url": "https://i.example/360.jpg", "height": 360},
               {"url": "https://i.example/240.jpg", "height": 240}
             ]}
            """);

        Assert.That(YouTubeSourceProvider.PickThumbnail(root),
            Is.EqualTo("https://i.example/360.jpg"),
            "the tallest thumbnail is the playlist cover");
    }

    [Test]
    public void PickThumbnail_ThumbnailsWithoutHeight_StillUsable()
    {
        var root = Root("""
            {"title": "p",
             "thumbnails": [{"url": "https://i.example/no-height.jpg"}]}
            """);

        Assert.That(YouTubeSourceProvider.PickThumbnail(root),
            Is.EqualTo("https://i.example/no-height.jpg"),
            "an entry without a height is still a valid cover");
    }

    [Test]
    public void PickThumbnail_FallsBackToRootThumbnailString()
    {
        var root = Root("""
            {"title": "p", "thumbnail": "https://i.example/root.jpg"}
            """);

        Assert.That(YouTubeSourceProvider.PickThumbnail(root),
            Is.EqualTo("https://i.example/root.jpg"),
            "some extracts expose only the flat thumbnail string");
    }

    [Test]
    public void PickThumbnail_FallsBackToFirstEntryThumbnail()
    {
        var root = Root("""
            {"title": "p",
             "entries": [
               {"id": "a", "thumbnail": "https://i.example/first.jpg"},
               {"id": "b", "thumbnail": "https://i.example/second.jpg"}
             ]}
            """);

        Assert.That(YouTubeSourceProvider.PickThumbnail(root),
            Is.EqualTo("https://i.example/first.jpg"),
            "with no playlist-level cover the first entry stands in");
    }

    [Test]
    public void PickThumbnail_ArrayBeatsFallbacks()
    {
        // Both a thumbnails array and a flat string: the array wins.
        var root = Root("""
            {"title": "p", "thumbnail": "https://i.example/flat.jpg",
             "thumbnails": [{"url": "https://i.example/array.jpg", "height": 90}]}
            """);

        Assert.That(YouTubeSourceProvider.PickThumbnail(root),
            Is.EqualTo("https://i.example/array.jpg"));
    }

    [Test]
    public void PickThumbnail_NoThumbnailsAnywhere_ReturnsNull()
    {
        var root = Root("""{"title": "p", "entries": [{"id": "a"}]}""");

        Assert.That(YouTubeSourceProvider.PickThumbnail(root), Is.Null,
            "no cover is better than a made-up one");
    }

    [Test]
    public void ParsePlaylistJson_RealFlatShape_MapsTracksAndCover()
    {
        var playlist = YouTubeSourceProvider.ParsePlaylistJson("""
            {
              "id": "PL123",
              "title": "My Mix",
              "channel": "Some Channel",
              "description": "desc",
              "thumbnails": [
                {"url": "https://i.example/small.jpg", "height": 120},
                {"url": "https://i.example/cover.jpg", "height": 404}
              ],
              "entries": [
                {"id": "aaa", "title": "First Song", "uploader": "Artist One", "duration": 213.5},
                {"id": "bbb", "title": "Second Song", "channel": "Artist Two", "duration": 180}
              ]
            }
            """, "https://www.youtube.com/playlist?list=PL123");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Id, Is.EqualTo("PL123"));
            Assert.That(playlist.Name, Is.EqualTo("My Mix"));
            Assert.That(playlist.Owner, Is.EqualTo("Some Channel"));
            Assert.That(playlist.CoverImageUrl, Is.EqualTo("https://i.example/cover.jpg"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));

            var first = playlist.Tracks![0];
            Assert.That(first.Title, Is.EqualTo("First Song"));
            Assert.That(first.Artist, Is.EqualTo("Artist One"), "the uploader is the artist");
            Assert.That(first.Duration, Is.EqualTo(TimeSpan.FromSeconds(213.5)));
            Assert.That(first.PlatformSongID!.ID, Is.EqualTo("aaa"));
            Assert.That(first.PlatformSongID.Platform, Is.EqualTo(Platform.YouTubeMusic));
            Assert.That(first.CurrentTrackLocation!.Path,
                Is.EqualTo("https://www.youtube.com/watch?v=aaa"));

            Assert.That(playlist.Tracks[1].Artist, Is.EqualTo("Artist Two"),
                "channel is the fallback when no uploader is present");
        });
    }

    [Test]
    public void ParsePlaylistJson_UnavailableEntries_AreSkipped()
    {
        var playlist = YouTubeSourceProvider.ParsePlaylistJson("""
            {
              "title": "Mix",
              "entries": [
                null,
                {"id": "", "title": "No id"},
                {"title": "No id either"},
                {"id": "ok", "title": "Real", "uploader": "A", "duration": 120}
              ]
            }
            """, "https://www.youtube.com/playlist?list=PL1");

        Assert.That(playlist.Tracks, Has.Count.EqualTo(1),
            "null stubs and id-less entries never become tracks");
        Assert.That(playlist.Tracks![0].Title, Is.EqualTo("Real"));
        Assert.That(playlist.TrackCount, Is.EqualTo(1));
    }

    [Test]
    public void ParsePlaylistJson_NoEntries_YieldsAnEmptyPlaylist()
    {
        var playlist = YouTubeSourceProvider.ParsePlaylistJson(
            """{"title": "Empty", "entries": []}""",
            "https://www.youtube.com/playlist?list=PL2");

        Assert.That(playlist.Name, Is.EqualTo("Empty"));
        Assert.That(playlist.Tracks, Is.Empty);
        Assert.That(playlist.TrackCount, Is.EqualTo(0));
    }

    [Test]
    public void ParsePlaylistJson_UnreadableJson_ThrowsAClearError()
    {
        Assert.That(
            () => YouTubeSourceProvider.ParsePlaylistJson("not json at all", "https://x/y?list=PL3"),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("unreadable JSON"));
    }
}
