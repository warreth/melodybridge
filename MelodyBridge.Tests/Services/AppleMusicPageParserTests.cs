using MelodyBridge.Infrastructure.AppleMusic;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class AppleMusicPageParserTests
{
    [Test]
    public void Parse_RealPageShape_MapsMetadataAndTracks()
    {
        var playlist = AppleMusicPageParser.Parse(
            AppleMusicTestData.PlaylistPageHtml(),
            AppleMusicTestData.PlaylistId,
            "https://music.apple.com/us/playlist/pl.test");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Page List"));
            Assert.That(playlist.Owner, Is.EqualTo("Page Curator"));
            Assert.That(playlist.Id, Is.EqualTo(AppleMusicTestData.PlaylistId));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Duration, Is.EqualTo(TimeSpan.FromMinutes(6)));
            Assert.That(playlist.Warning, Is.Null);
            Assert.That(playlist.CoverImageUrl, Does.Contain("600x600bb.jpg"));
        });

        var first = playlist.Tracks![0];
        Assert.Multiple(() =>
        {
            Assert.That(first.Title, Is.EqualTo("Page Track 1"));
            Assert.That(first.Artist, Is.EqualTo("Page Artist 1"));
            Assert.That(first.Album, Is.EqualTo("Page Album"));
            Assert.That(first.PlatformSongID!.ID, Is.EqualTo("1"));
            Assert.That(first.Isrc, Is.Null, "the page has no ISRC; that is the API path's job");
            Assert.That(first.Duration, Is.EqualTo(TimeSpan.FromMinutes(3)));
        });
    }

    [Test]
    public void Parse_FewerTracksThanDeclared_SetsAPartialWarning()
    {
        var playlist = AppleMusicPageParser.Parse(
            AppleMusicTestData.PlaylistPageHtml(declaredCount: 3, trackCount: 2),
            AppleMusicTestData.PlaylistId,
            "https://music.apple.com/us/playlist/pl.test");

        Assert.That(playlist.Warning, Does.Contain("read 2 of 3"));
    }

    [Test]
    public void Parse_MissingSerializedData_ThrowsThePublicMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppleMusicPageParser.Parse("<html></html>", "pl.test", "https://music.apple.com/us/playlist/pl.test"));
        Assert.That(ex!.Message, Does.Contain("public or shared"));
    }

    [Test]
    public void Parse_EmptyTrackListWithDeclaredTracks_ThrowsThePublicMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppleMusicPageParser.Parse(
                AppleMusicTestData.PlaylistPageHtml(declaredCount: 2, trackCount: 0),
                "pl.test",
                "https://music.apple.com/us/playlist/pl.test"));
        Assert.That(ex!.Message, Does.Contain("listed no tracks"));
    }
}
