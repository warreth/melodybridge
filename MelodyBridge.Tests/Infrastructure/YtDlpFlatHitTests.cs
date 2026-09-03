using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Offline tests for the yt-dlp flat-playlist hit parser, the piece every
/// yt-dlp-backed plugin (YouTube, SoundCloud) uses to turn search output
/// into a hit. The artist used to be the hit TITLE, so "Regard" was
/// compared with "Regard - Ride It (Official Video)" and every YouTube
/// search came back Low confidence.
/// </summary>
[TestFixture]
public class YtDlpFlatHitTests
{
    private static DownloaderSearchHit? Parse(object entry)
    {
        var json = JsonSerializer.Serialize(new
        {
            entries = new[] { entry },
        });
        return YtDlpDownloader.ParseFlatPlaylistHit(json, "Regard", "Ride It",
            id => $"https://www.youtube.com/watch?v={id}");
    }

    [Test]
    public void UploaderField_BecomesTheHitArtist_DecorationsStripped()
    {
        var hit = Parse(new
        {
            id = "dQw4w9WgXcQ",
            title = "Regard - Ride It (Official Video)",
            uploader = "RegardVEVO",
            duration = 189,
        });

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Artist, Is.EqualTo("Regard"),
            "the uploader is the artist, with channel decorations stripped");
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High),
            "stripped, the uploader matches the requested artist");
    }

    [Test]
    public void ChannelField_IsTheArtistFallback()
    {
        var hit = Parse(new
        {
            id = "abc123",
            title = "Ride It",
            channel = "Regard - Topic",
            duration = 189,
        });

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Artist, Is.EqualTo("Regard"),
            "the - Topic suffix is YouTube's, not the artist's");
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
    }

    [Test]
    public void NoUploaderOrChannel_SplitsArtistFromTitle()
    {
        var hit = Parse(new
        {
            id = "abc123",
            title = "Regard - Ride It",
            duration = 189,
        });

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Artist, Is.EqualTo("Regard"),
            "an Artist - Title string still yields the artist");
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
    }

    [Test]
    public void FullUrl_PrefersWebpageUrlOverBareId()
    {
        var hit = Parse(new
        {
            id = "abc123",
            title = "Ride It",
            webpage_url = "https://soundcloud.com/regard/ride-it",
            uploader = "Regard",
        });

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://soundcloud.com/regard/ride-it"));
    }

    [Test]
    public void MalformedJson_ReturnsNull()
    {
        Assert.That(YtDlpDownloader.ParseFlatPlaylistHit(
            "not json at all", "A", "T", id => id), Is.Null);
    }
}
