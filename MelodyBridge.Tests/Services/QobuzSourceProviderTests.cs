using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.SearchProviders;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class QobuzSourceProviderTests
{
    private const long PlaylistId = 52330829;

    [TestCase("https://play.qobuz.com/playlist/52330829")]
    [TestCase("https://www.qobuz.com/nl-nl/playlist/52330829")]
    [TestCase("qobuz:playlist:52330829")]
    public void CanHandle_QobuzPlaylistUrls(string input)
        => Assert.That(NewProvider(new ScriptedHandler(), "token").CanHandle(input), Is.True);

    [Test]
    public void CanHandle_AlbumAndTrackUrls_AreRefused()
    {
        var provider = NewProvider(new ScriptedHandler(), "token");
        Assert.Multiple(() =>
        {
            Assert.That(provider.CanHandle("https://www.qobuz.com/nl-nl/album/abc/123"), Is.False);
            Assert.That(provider.CanHandle("https://play.qobuz.com/album/123"), Is.False);
        });
    }

    [Test]
    public async Task GetPlaylist_MapsQobuzMetadataAndTracks()
    {
        var handler = QobuzHandler();
        var playlist = await NewProvider(handler, "token").GetPlaylistAsync(
            "https://play.qobuz.com/playlist/52330829");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Qobuz List"));
            Assert.That(playlist.Owner, Is.EqualTo("Owner"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.CoverImageUrl, Is.EqualTo("https://images.qobuz.test/300"));
            Assert.That(playlist.Tracks![0].Isrc, Is.EqualTo("USAAA0000011"));
            Assert.That(playlist.Tracks[0].Album, Is.EqualTo("Album"));
            Assert.That(playlist.Tracks[0].CoverUrl, Is.EqualTo("https://images.qobuz.test/large"));
            Assert.That(playlist.Tracks[0].SourcePlatform,
                Is.EqualTo(MelodyBridge.Core.Platform.Qobuz));
        });
    }

    [Test]
    public void GetPlaylist_WithoutAToken_ExplainsWhereToAddIt()
    {
        var provider = NewProvider(new ScriptedHandler(), token: null);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetPlaylistAsync("https://play.qobuz.com/playlist/52330829"));
        Assert.That(ex!.Message, Does.Contain("Qobuz token"));
    }

    [Test]
    public void GetPlaylist_NotFound_SaysItMustBePublic()
    {
        var handler = QobuzHandler(playlistStatus: System.Net.HttpStatusCode.NotFound);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProvider(handler, "token").GetPlaylistAsync(
                "https://play.qobuz.com/playlist/52330829"));
        Assert.That(ex!.Message, Does.Contain("must be public"));
    }

    private static ScriptedHandler QobuzHandler(
        System.Net.HttpStatusCode playlistStatus = System.Net.HttpStatusCode.OK)
        => new ScriptedHandler()
            .On("/login", """
                <html><script src="/resources/1.0.0-a000/bundle.js"></script></html>
                """)
            .On("/resources/1.0.0-a000/bundle.js", """
                production:{api:{appId:"798273057",appSecret:"0123456789abcdef0123456789abcdef"}}
                privateKey:"abcdef"
                """)
            .On("/playlist/get", _ => new HttpResponseMessage(playlistStatus)
            {
                Content = new StringContent(playlistStatus == System.Net.HttpStatusCode.OK
                    ? """
                      {"id":52330829,"name":"Qobuz List","description":"Desc",
                       "owner":{"display_name":"Owner"},"tracks_count":2,"duration":600,
                       "images300":"https://images.qobuz.test/300",
                       "tracks":{"total":2,"offset":0,"limit":500,"items":[
                         {"id":11,"title":"One","duration":180,"isrc":"USAAA0000011",
                          "performer":{"name":"Artist"},"maximum_bit_depth":16,
                          "maximum_sampling_rate":44.1,"hires_streamable":false,
                          "album":{"id":"a1","title":"Album",
                                   "image":{"large":"https://images.qobuz.test/large"}}},
                         {"id":12,"title":"Two","duration":200,"isrc":"USAAA0000012",
                          "performer":{"name":"Artist"},
                          "album":{"id":"a1","title":"Album",
                                   "image":{"large":"https://images.qobuz.test/large"}}}
                       ]}}
                      """
                    : """{"code":404,"message":"not found","status":"error"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });

    private static QobuzSourceProvider NewProvider(ScriptedHandler handler, string? token)
    {
        var config = new StubPluginConfig();
        if (token is not null) config.Set(QobuzSession.PluginId, QobuzSession.TokenKey, token);
        var client = new QobuzClient(
            new HttpClient(handler), NullLogger<QobuzClient>.Instance);
        var session = new QobuzSession(
            config, NullLogger<QobuzSession>.Instance, "/tmp/mb-qobuz-no-home");
        return new QobuzSourceProvider(
            client, session, NullLogger<QobuzSourceProvider>.Instance);
    }
}
