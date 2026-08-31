using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class SpotifySourceProviderTests
{
    private const string PlaylistId = "2O9RpWPbupc8NrX4T5ZXia";
    private const string PlaylistUrl = "https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia?si=88y_XcMvSIuPe2oznpjWEw";

    [TestCase("https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia?si=abc", PlaylistId)]
    [TestCase("https://open.spotify.com/embed/playlist/2O9RpWPbupc8NrX4T5ZXia?utm_source=oembed", PlaylistId)]
    [TestCase("spotify:playlist:2O9RpWPbupc8NrX4T5ZXia", PlaylistId)]
    [TestCase(PlaylistId, PlaylistId)]
    public void ExtractPlaylistId_SupportedInputs_ReturnsId(string input, string expected)
    {
        Assert.That(SpotifySourceProvider.ExtractPlaylistId(input), Is.EqualTo(expected));
    }

    [Test]
    public void ExtractPlaylistId_InvalidInput_ThrowsHelpfulError()
    {
        var ex = Assert.Throws<ArgumentException>(() => SpotifySourceProvider.ExtractPlaylistId("https://example.com/not-spotify"));
        Assert.That(ex!.Message, Does.Contain("Spotify playlist ID"));
    }

    [TestCase("https://open.spotify.com/user/18meb51u", "18meb51u")]
    [TestCase("spotify:user:18meb51u", "18meb51u")]
    [TestCase("18meb51u", "18meb51u")]
    public void ExtractUserId_SupportedInputs_ReturnsUserId(string input, string expected)
    {
        Assert.That(SpotifySourceProvider.ExtractUserId(input), Is.EqualTo(expected));
    }

    [Test]
    public void ExtractPlaylistUrls_UserPageHtml_ReturnsDistinctPublicPlaylistUrls()
    {
        const string html = """
        <a href="/playlist/2O9RpWPbupc8NrX4T5ZXia">Test-Playlist</a>
        <a href="https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia?si=duplicate">Duplicate</a>
        <a href="/playlist/37i9dQZF1DXcBWIGoYBM5M">Another Playlist</a>
        """;

        var urls = SpotifySourceProvider.ExtractPlaylistUrls(html);

        Assert.That(urls, Is.EquivalentTo(new[]
        {
        "https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia",
        "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M",
      }));
    }

    [Test]
    public void ParseEmbedPlaylistHtml_ProvidedPlaylistFixture_ReturnsExpectedMetadataAndTracks()
    {
        var playlist = SpotifySourceProvider.ParseEmbedPlaylistHtml(CreateNextDataHtml(), PlaylistId, PlaylistUrl);

        Assert.That(playlist, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(playlist!.Id, Is.EqualTo(PlaylistId));
            Assert.That(playlist.Name, Is.EqualTo("Test-Playlist"));
            Assert.That(playlist.Owner, Is.EqualTo("18meb51u"));
            Assert.That(playlist.SourceUrl, Is.EqualTo(PlaylistUrl));
            Assert.That(playlist.CoverImageUrl, Is.EqualTo("https://i.scdn.co/image/ab67616d00001e02cadabfbf259176b0720e391b"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(216733 + 188066)));
        });

        Assert.Multiple(() =>
        {
            Assert.That(playlist!.Tracks![0].Title, Is.EqualTo("Für Elise, WoO 59"));
            Assert.That(playlist.Tracks[0].Artist, Is.EqualTo("Ludwig van Beethoven, Rudolf Buchbinder"));
            Assert.That(playlist.Tracks[0].SongID, Is.EqualTo(new SongID(Platform.Spotify, "0JGlKik7pi4Rwuzqpjohtj")));
            Assert.That(playlist.Tracks[0].CurrentTrackLocation!.Path, Is.EqualTo("https://open.spotify.com/track/0JGlKik7pi4Rwuzqpjohtj"));
            Assert.That(playlist.Tracks[0].Duration, Is.EqualTo(TimeSpan.FromMilliseconds(216733)));

            Assert.That(playlist.Tracks[1].Title, Is.EqualTo("The Four Seasons, Violin Concerto in E Major, Op. 8 No. 1, RV 269 \"Spring\": I. Allegro"));
            Assert.That(playlist.Tracks[1].Artist, Is.EqualTo("Antonio Vivaldi, Renaud Capuçon, Orchestre de Chambre de Lausanne"));
            Assert.That(playlist.Tracks[1].SongID, Is.EqualTo(new SongID(Platform.Spotify, "5o2TFgKHGOUDhXuLSoGhav")));
            Assert.That(playlist.Tracks[1].Duration, Is.EqualTo(TimeSpan.FromMilliseconds(188066)));
        });
    }

    [Test]
    public void ParseVisiblePlaylistHtml_FallbackMarkup_ReturnsTracks()
    {
        var html = """
            <html><body>
            <h1>Test-Playlist</h1>
            <li data-testid="tracklist-row-0"><h3>Für Elise, WoO 59</h3><h4>Ludwig van Beethoven,&nbsp;Rudolf Buchbinder</h4><div data-testid="duration-cell">03:36</div></li>
            <li data-testid="tracklist-row-1"><h3>The Four Seasons &quot;Spring&quot;: I. Allegro</h3><h4>Antonio Vivaldi,&nbsp;Renaud Capuçon</h4><div data-testid="duration-cell">03:08</div></li>
            </body></html>
            """;

        var playlist = SpotifySourceProvider.ParseVisiblePlaylistHtml(html, PlaylistId, PlaylistUrl);

        Assert.That(playlist, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(playlist!.Id, Is.EqualTo(PlaylistId));
            Assert.That(playlist.Name, Is.EqualTo("Test-Playlist"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Tracks![0].Artist, Is.EqualTo("Ludwig van Beethoven, Rudolf Buchbinder"));
            Assert.That(playlist.Tracks[1].Title, Is.EqualTo("The Four Seasons \"Spring\": I. Allegro"));
            Assert.That(playlist.Duration, Is.EqualTo(TimeSpan.FromSeconds(216 + 188)));
        });
    }

    [Test]
    public async Task GetPlaylistAsync_PublicEmbedEndpoints_ReturnsExpectedPlaylist()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/embed/playlist/"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CreateNextDataHtml()) };
            if (uri.Contains("/oembed"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                    {"title":"Test-Playlist","thumbnail_url":"https://i.scdn.co/image/ab67616d00001e02cadabfbf259176b0720e391b"}
                    """) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new SpotifySourceProvider(
            NullLogger<SpotifySourceProvider>.Instance,
            null, // configuration
            new HttpClient(handler));

        var playlist = await provider.GetPlaylistAsync(PlaylistUrl);

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Id, Is.EqualTo(PlaylistId));
            Assert.That(playlist.Name, Is.EqualTo("Test-Playlist"));
            Assert.That(playlist.Owner, Is.EqualTo("18meb51u"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task GetUserPlaylistUrlsAsync_PublicUserPage_ReturnsPlaylistUrls()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("/user/18meb51u/playlists"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                <a href="/playlist/2O9RpWPbupc8NrX4T5ZXia">Test-Playlist</a>
                <a href="/playlist/37i9dQZF1DXcBWIGoYBM5M">Another Playlist</a>
                """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new SpotifySourceProvider(
          NullLogger<SpotifySourceProvider>.Instance,
          null, // configuration
          new HttpClient(handler));

        var urls = await provider.GetUserPlaylistUrlsAsync("https://open.spotify.com/user/18meb51u");

        Assert.That(urls, Is.EquivalentTo(new[]
        {
          "https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia",
          "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M",
        }));
    }

    [Test]
    [Category("External")]
    public async Task GetPlaylistAsync_RealPublicPlaylist_ReturnsExpectedTracks()
    {
        var provider = new SpotifySourceProvider(NullLogger<SpotifySourceProvider>.Instance);

        var playlist = await provider.GetPlaylistAsync(PlaylistUrl);

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Id, Is.EqualTo(PlaylistId));
            Assert.That(playlist.Name, Is.EqualTo("Test-Playlist"));
            Assert.That(playlist.Owner, Is.EqualTo("18meb51u"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Tracks![0].Title, Is.EqualTo("Für Elise, WoO 59"));
            Assert.That(playlist.Tracks[0].Artist, Does.Contain("Ludwig van Beethoven"));
            Assert.That(playlist.Tracks[1].Title, Does.Contain("The Four Seasons"));
            Assert.That(playlist.Tracks[1].Artist, Does.Contain("Antonio Vivaldi"));
        });
    }

    /// <summary>
    /// The reference Techno playlist has 101 tracks. The old embed scrape
    /// capped at 100; the API path must return every track.
    /// </summary>
    [Test]
    [Category("External")]
    public async Task GetPlaylistAsync_PlaylistOver100Tracks_ReturnsAll()
    {
        var provider = new SpotifySourceProvider(NullLogger<SpotifySourceProvider>.Instance);

        var playlist = await provider.GetPlaylistAsync("https://open.spotify.com/playlist/55V41RYWVdALRiiN1onkUr");

        if (playlist.Tracks.Count == 100)
        {
            // The API path is quota-limited per IP per day. When the quota is
            // spent, the provider honestly falls back to the embed scrape and
            // that is capped at 100. Only the API path can exceed it.
            Assert.Inconclusive(
                "API quota exhausted from this IP (Spotify QUOTA_EXCEEDED, ~24h). " +
                "Rerun on a fresh IP to validate the >100 path.");
        }

        Assert.That(playlist.TrackCount, Is.EqualTo(101), "the source playlist has 101 tracks");
        Assert.That(playlist.Tracks, Has.Count.EqualTo(101),
            "all 101 tracks must come back, not the embed page 100 cap");
        Assert.That(playlist.Name, Is.EqualTo("Techno"));
    }

    private static string CreateNextDataHtml() => """
        <!DOCTYPE html><html><body>
        <script id="__NEXT_DATA__" type="application/json">{
          "props": {
            "pageProps": {
              "state": {
                "data": {
                  "entity": {
                    "type": "playlist",
                    "name": "Test-Playlist",
                    "uri": "spotify:playlist:2O9RpWPbupc8NrX4T5ZXia",
                    "id": "2O9RpWPbupc8NrX4T5ZXia",
                    "title": "Test-Playlist",
                    "subtitle": "18meb51u",
                    "description": "Public Playlist",
                    "coverArt": {
                      "sources": [
                        { "height": null, "width": null, "url": "https://i.scdn.co/image/ab67616d00001e02cadabfbf259176b0720e391b" }
                      ]
                    },
                    "trackList": [
                      {
                        "uri": "spotify:track:0JGlKik7pi4Rwuzqpjohtj",
                        "uid": "f1c983657e95bd2a",
                        "title": "Für Elise, WoO 59",
                        "subtitle": "Ludwig van Beethoven, Rudolf Buchbinder",
                        "duration": 216733,
                        "entityType": "track"
                      },
                      {
                        "uri": "spotify:track:5o2TFgKHGOUDhXuLSoGhav",
                        "uid": "5eb93032ac4ecf62",
                        "title": "The Four Seasons, Violin Concerto in E Major, Op. 8 No. 1, RV 269 \"Spring\": I. Allegro",
                        "subtitle": "Antonio Vivaldi, Renaud Capuçon, Orchestre de Chambre de Lausanne",
                        "duration": 188066,
                        "entityType": "track"
                      }
                    ]
                  }
                }
              }
            }
          }
        }</script>
        </body></html>
        """;

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
