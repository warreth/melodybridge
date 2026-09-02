using System.Text.Json;
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
}
