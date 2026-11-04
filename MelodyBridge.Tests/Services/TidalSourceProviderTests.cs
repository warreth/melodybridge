using MelodyBridge.Infrastructure.Tidal;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class TidalSourceProviderTests
{
    private const string PlaylistId = "5b648240-d0de-43d1-a09a-01f84ee1383e";

    [TestCase("https://tidal.com/browse/playlist/5b648240-d0de-43d1-a09a-01f84ee1383e")]
    [TestCase("https://listen.tidal.com/browse/playlist/5b648240-d0de-43d1-a09a-01f84ee1383e")]
    [TestCase("tidal:playlist:5b648240-d0de-43d1-a09a-01f84ee1383e")]
    public void CanHandle_TidalPlaylistUrls(string input)
        => Assert.That(NewProvider(new ScriptedHandler()).CanHandle(input), Is.True);

    [Test]
    public void CanHandle_AlbumAndTrackUrls_AreRefused()
    {
        var provider = NewProvider(new ScriptedHandler());
        Assert.Multiple(() =>
        {
            Assert.That(provider.CanHandle("https://tidal.com/browse/album/123"), Is.False);
            Assert.That(provider.CanHandle("https://tidal.com/browse/track/123"), Is.False);
        });
    }

    [Test]
    public async Task GetPlaylist_MapsTidalMetadataAndTracks()
    {
        var handler = new ScriptedHandler()
            .On("/token", """{"access_token":"token","expires_in":3600}""")
            .On($"/playlists/{PlaylistId}/tracks", """
                {"limit":50,"offset":0,"totalNumberOfItems":2,"items":[
                  {"id":11,"title":"One","duration":180,"isrc":"USAAA0000011",
                   "streamReady":true,"artist":{"name":"Artist"},"album":{"title":"Album","cover":"cover-id"}},
                  {"id":12,"title":"Two","duration":200,"isrc":"USAAA0000012",
                   "streamReady":true,"artist":{"name":"Artist"},"album":{"title":"Album","cover":"cover-id"}}
                ]}
                """)
            .On($"/playlists/{PlaylistId}", $$"""
                {"uuid":"{{PlaylistId}}","title":"Big Hits","description":"Desc",
                 "numberOfTracks":2,"duration":380,"creator":{"name":"NFL"},
                 "squareImage":"1234-5678-90ab-cdef"}
                """);

        var playlist = await NewProvider(handler).GetPlaylistAsync(
            $"https://tidal.com/browse/playlist/{PlaylistId}");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Big Hits"));
            Assert.That(playlist.Owner, Is.EqualTo("NFL"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Tracks![0].Isrc, Is.EqualTo("USAAA0000011"));
            Assert.That(playlist.Tracks[0].Album, Is.EqualTo("Album"));
            Assert.That(playlist.Tracks[0].CoverUrl, Does.Contain("resources.tidal.com"));
            Assert.That(playlist.Tracks[0].SourcePlatform, Is.EqualTo(MelodyBridge.Core.Platform.Tidal));
        });
    }

    [Test]
    public void GetPlaylist_NotFound_SaysItMustBePublic()
    {
        var handler = new ScriptedHandler()
            .On("/token", """{"access_token":"token","expires_in":3600}""")
            .On($"/playlists/{PlaylistId}", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProvider(handler).GetPlaylistAsync($"https://tidal.com/browse/playlist/{PlaylistId}"));
        Assert.That(ex!.Message, Does.Contain("must be public"));
    }

    private static TidalSourceProvider NewProvider(ScriptedHandler handler)
    {
        var catalog = new TidalPublicCatalog(
            new HttpClient(handler), NullLogger<TidalPublicCatalog>.Instance);
        return new TidalSourceProvider(
            catalog, new StubPluginConfig(), NullLogger<TidalSourceProvider>.Instance);
    }
}
