using System.Net;
using System.Text;
using MelodyBridge.Infrastructure.AppleMusic;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class AppleMusicSourceProviderTests
{
    private ScriptedHandler _handler = null!;
    private AppleMusicSourceProvider _provider = null!;

    [TearDown]
    public void TearDown() => _handler.Dispose();

    [SetUp]
    public void Setup()
    {
        _handler = new ScriptedHandler()
            .On("/assets/index", AppleMusicTestData.Bundle)
            .On("/", AppleMusicTestData.HomeHtml);

        var tokens = AppleMusicTestData.TokenProvider(_handler);
        var api = new AppleMusicApiClient(
            new HttpClient(_handler), tokens,
            NullLogger<AppleMusicApiClient>.Instance)
        {
            MaxAttempts = 2,
            RetryBaseDelay = TimeSpan.Zero,
        };
        _provider = new AppleMusicSourceProvider(
            api,
            NullLogger<AppleMusicSourceProvider>.Instance,
            new HttpClient(_handler));
    }

    [Test]
    public async Task GetPlaylistAsync_ApiPath_MapsIsrcAndPaging()
    {
        _handler.On("/v1/catalog/us/playlists/pl.test/tracks",
            AppleMusicTestData.TracksPageJson(
                null,
                ("12", "Two", "Artist Two", "USAAA0000012")));
        _handler.On("/v1/catalog/us/playlists/pl.test",
            AppleMusicTestData.PlaylistApiJson(
                next: "/v1/catalog/us/playlists/pl.test/tracks?offset=1",
                tracks: new[] { ("11", "One", "Artist One", "USAAA0000011") }));

        var playlist = await _provider.GetPlaylistAsync(
            "https://music.apple.com/us/playlist/pl.test");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("API List"));
            Assert.That(playlist.Owner, Is.EqualTo("Curator"));
            Assert.That(playlist.Description, Is.EqualTo("A test playlist"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Tracks![0].Isrc, Is.EqualTo("USAAA0000011"));
            Assert.That(playlist.Tracks[0].AlbumArtist, Is.EqualTo("Album Artist"));
            Assert.That(playlist.Tracks[1].Title, Is.EqualTo("Two"));
            Assert.That(playlist.CoverImageUrl, Does.Contain("600x600bb.jpg"));
        });

        Assert.That(_handler.Requests.Any(r =>
            r.Url.Contains("limit[tracks]=300") || r.Url.Contains("limit%5Btracks%5D=300")),
            Is.True, "the API request asks Apple for the 300-track page");
    }

    [Test]
    public async Task GetPlaylistAsync_ApiFailure_FallsBackToThePublicPage()
    {
        _handler.On("/v1/catalog", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        _handler.On("/us/playlist", AppleMusicTestData.PlaylistPageHtml());

        var playlist = await _provider.GetPlaylistAsync(
            "https://music.apple.com/us/playlist/pl.test");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Page List"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks![0].Title, Is.EqualTo("Page Track 1"));
        });
        Assert.That(_handler.Requests.Any(r => r.Url.Contains("/us/playlist")), Is.True,
            "the fallback read the public page");
    }

    [Test]
    public void GetPlaylistAsync_ApiNotFound_DoesNotFallBackAndExplainsWhy()
    {
        _handler.On("/v1/catalog", _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"errors":[{"detail":"Resource Not Found"}]}""",
                Encoding.UTF8, "application/json"),
        });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetPlaylistAsync("https://music.apple.com/us/playlist/pl.test"));

        Assert.That(ex!.Message, Does.Contain("public or shared"));
    }

    [Test]
    public void CanHandle_PlaylistUrls_TrueAndAlbumUrls_False()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_provider.CanHandle("https://music.apple.com/us/playlist/x/pl.test"), Is.True);
            Assert.That(_provider.CanHandle("pl.u-test"), Is.True);
            Assert.That(_provider.CanHandle("https://music.apple.com/us/album/x/1?i=2"), Is.False);
            Assert.That(_provider.CanHandle("https://music.apple.com/us/song/x/2"), Is.False);
        });
    }

    [Test]
    public void GetPlaylistAsync_LibraryMarker_ExplainsTheAccountRoute()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.GetPlaylistAsync("applemusic:library:p.one"));
        Assert.That(ex!.Message, Does.Contain("Connect an Apple Music"));
    }
}
