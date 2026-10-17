using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The Deezer playlist source against a scripted HTTP boundary: the public
/// payload shape (captured from the live API), pagination over tracks.next
/// and the identifier rules.
/// </summary>
[TestFixture]
public class DeezerSourceProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request.RequestUri!.ToString()));
        }
    }

    private static DeezerSourceProvider Create(StubHandler handler)
        => new(new HttpClient(handler), NullLogger<DeezerSourceProvider>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private const string PlaylistPayload = """
    {
      "id": 3155776842,
      "title": "Top Worldwide",
      "description": "The biggest tracks right now",
      "nb_tracks": 2,
      "picture_big": "https://cdn-images.dzcdn.net/images/playlist/abc/1000x1000.jpg",
      "creator": { "id": 1, "name": "Deezer Charts" },
      "tracks": {
        "data": [
          {
            "id": 1234567,
            "title": "Around the World",
            "duration": 429,
            "isrc": "GBDUW0600009",
            "artist": { "id": 27, "name": "Daft Punk" },
            "album": { "id": 99, "title": "Homework" }
          },
          {
            "id": 7654321,
            "title": "One More Time",
            "duration": 320,
            "isrc": "GBDUW0000053",
            "artist": { "id": 27, "name": "Daft Punk" },
            "album": { "id": 98, "title": "Discovery" }
          }
        ],
        "next": null
      }
    }
    """;

    [TestCase("https://www.deezer.com/playlist/3155776842", true)]
    [TestCase("https://deezer.com/nl/playlist/3155776842?utm=1", true)]
    [TestCase("https://www.deezer.com/track/3135556", false)]
    [TestCase("https://open.spotify.com/playlist/abc", false)]
    [TestCase("", false)]
    public void CanHandle_OnlyDeezerPlaylists(string input, bool expected)
        => Assert.That(Create(new StubHandler()).CanHandle(input), Is.EqualTo(expected));

    [TestCase("https://www.deezer.com/playlist/3155776842", "3155776842")]
    [TestCase("https://deezer.com/nl/playlist/3155776842?utm=1", "3155776842")]
    [TestCase("https://www.deezer.com/track/3135556", null)]
    public void ExtractPlaylistId_ReadsTheId(string input, string? expected)
        => Assert.That(DeezerSourceProvider.ExtractPlaylistId(input), Is.EqualTo(expected));

    [Test]
    public void ParsePlaylist_MapsTheRealShape()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PlaylistPayload);
        var playlist = DeezerSourceProvider.ParsePlaylist(doc.RootElement, "https://www.deezer.com/playlist/3155776842");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Top Worldwide"));
            Assert.That(playlist.Owner, Is.EqualTo("Deezer Charts"));
            Assert.That(playlist.CoverImageUrl, Does.StartWith("https://cdn-images.dzcdn.net/"));
            Assert.That(playlist.TrackCount, Is.EqualTo(2));
            Assert.That(playlist.Warning, Is.Null);
        });

        var first = playlist.Tracks![0];
        Assert.Multiple(() =>
        {
            Assert.That(first.Title, Is.EqualTo("Around the World"));
            Assert.That(first.Artist, Is.EqualTo("Daft Punk"));
            Assert.That(first.Album, Is.EqualTo("Homework"));
            Assert.That(first.Isrc, Is.EqualTo("GBDUW0600009"),
                "Deezer's public payload carries the ISRC, the strongest matching signal");
            Assert.That(first.Duration, Is.EqualTo(TimeSpan.FromSeconds(429)));
            Assert.That(first.SongID, Is.EqualTo(new SongID(Platform.Deezer, "1234567")));
            Assert.That(MelodyIds.For(first.SongID!), Is.EqualTo("deezer:1234567"),
                "the deterministic id keeps the platform prefix");
        });
    }

    [Test]
    public async Task GetPlaylistAsync_FetchesTheApiAndKeepsTheUrl()
    {
        var handler = new StubHandler { Respond = _ => Json(PlaylistPayload) };
        var playlist = await Create(handler).GetPlaylistAsync("https://www.deezer.com/playlist/3155776842");

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
            Assert.That(handler.Requests[0], Is.EqualTo("https://api.deezer.com/playlist/3155776842"));
            Assert.That(playlist.SourceUrl, Is.EqualTo("https://www.deezer.com/playlist/3155776842"));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task GetPlaylistAsync_FollowsTheNextPage()
    {
        var handler = new StubHandler
        {
            Respond = url =>
            {
                if (url.Contains("/tracks?"))
                {
                    // The second page is the last one.
                    return Json("""
                    {"data":[{"id":555,"title":"Page two track","duration":200,
                      "artist":{"name":"Someone"},"album":{"title":"Some album"}}],
                     "next":null}
                    """);
                }

                // The playlist payload points at that page.
                return Json(PlaylistPayload.Replace(
                    "\"next\": null",
                    "\"next\": \"https://api.deezer.com/playlist/3155776842/tracks?index=2\""));
            },
        };

        var playlist = await Create(handler).GetPlaylistAsync("https://www.deezer.com/playlist/3155776842");

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests.Count(r => r.Contains("/tracks?")), Is.EqualTo(1),
                "the provider walks tracks.next until it runs out");
            Assert.That(playlist.TrackCount, Is.EqualTo(3));
            Assert.That(playlist.Tracks!.Select(t => t.Title), Does.Contain("Page two track"));
        });
    }

    [Test]
    public void GetPlaylistAsync_NotAPlaylistUrl_Throws()
        => Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new StubHandler()).GetPlaylistAsync("https://www.deezer.com/track/3135556"));
}
