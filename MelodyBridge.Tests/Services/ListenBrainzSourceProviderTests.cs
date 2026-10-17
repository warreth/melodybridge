using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The ListenBrainz playlist source against a scripted HTTP boundary: the
/// JSPF payload shape (captured from the live API), the MBID rules and the
/// millisecond durations.
/// </summary>
[TestFixture]
public class ListenBrainzSourceProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public string Body { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ListenBrainzSourceProvider Create(StubHandler handler)
        => new(new HttpClient(handler), NullLogger<ListenBrainzSourceProvider>.Instance);

    private const string JspfPayload = """
    {
      "playlist": {
        "creator": "for_emma",
        "annotation": "A quiet evening",
        "identifier": "https://listenbrainz.org/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306",
        "title": "Jazz",
        "track": [
          {
            "album": "Tuesday Wonderland",
            "creator": "Esbjorn Svensson Trio",
            "duration": 267173,
            "identifier": [ "https://musicbrainz.org/recording/d1312a5d-e811-4f14-97bb-d5663ff6ef89" ],
            "title": "Where We Used to Live"
          }
        ]
      }
    }
    """;

    [TestCase("https://listenbrainz.org/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306", true)]
    [TestCase("6c732bd8-c510-4ae6-bbcf-5230dc26e306", true)]
    [TestCase("https://listenbrainz.org/user/for_emma", false)]
    [TestCase("https://open.spotify.com/playlist/abc", false)]
    [TestCase("", false)]
    public void CanHandle_PlaylistUrlsAndRawMbids(string input, bool expected)
        => Assert.That(Create(new StubHandler()).CanHandle(input), Is.EqualTo(expected));

    [TestCase("https://listenbrainz.org/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306",
        "6c732bd8-c510-4ae6-bbcf-5230dc26e306")]
    [TestCase("6c732bd8-c510-4ae6-bbcf-5230dc26e306", "6c732bd8-c510-4ae6-bbcf-5230dc26e306")]
    [TestCase("https://listenbrainz.org/playlist/not-a-mbid", null)]
    public void ExtractPlaylistMbid_ReadsTheId(string input, string? expected)
        => Assert.That(ListenBrainzSourceProvider.ExtractPlaylistMbid(input), Is.EqualTo(expected));

    [Test]
    public void ParsePlaylist_MapsTheRealJspfShape()
    {
        using var doc = JsonDocument.Parse(JspfPayload);
        var playlist = ListenBrainzSourceProvider.ParsePlaylist(
            doc.RootElement, "https://listenbrainz.org/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306");

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Jazz"));
            Assert.That(playlist.Owner, Is.EqualTo("for_emma"));
            Assert.That(playlist.Description, Is.EqualTo("A quiet evening"));
            Assert.That(playlist.TrackCount, Is.EqualTo(1));
        });

        var track = playlist.Tracks![0];
        Assert.Multiple(() =>
        {
            Assert.That(track.Title, Is.EqualTo("Where We Used to Live"));
            Assert.That(track.Artist, Is.EqualTo("Esbjorn Svensson Trio"));
            Assert.That(track.Album, Is.EqualTo("Tuesday Wonderland"));
            Assert.That(track.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(267173)),
                "JSPF durations are milliseconds");
            Assert.That(track.SongID!.ID, Is.EqualTo("mb:d1312a5d-e811-4f14-97bb-d5663ff6ef89"),
                "the recording MBID becomes a namespaced id");
            Assert.That(MelodyIds.For(track.SongID!), Does.StartWith("unknown:mb:"));
        });
    }

    [Test]
    public async Task GetPlaylistAsync_FetchesTheApi()
    {
        var handler = new StubHandler { Body = JspfPayload };
        var playlist = await Create(handler).GetPlaylistAsync(
            "https://listenbrainz.org/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306");

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
            Assert.That(handler.Requests[0], Is.EqualTo(
                "https://api.listenbrainz.org/1/playlist/6c732bd8-c510-4ae6-bbcf-5230dc26e306"));
            Assert.That(playlist.Name, Is.EqualTo("Jazz"));
        });
    }

    [Test]
    public void GetPlaylistAsync_UnknownPlaylist_Throws()
    {
        var handler = new StubHandler { Body = "{}" };
        Assert.ThrowsAsync<InvalidOperationException>(() => Create(handler).GetPlaylistAsync(
            "6c732bd8-c510-4ae6-bbcf-5230dc26e306"));
    }

    [Test]
    public void GetPlaylistAsync_NotAPlaylist_Throws()
        => Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(new StubHandler()).GetPlaylistAsync("https://listenbrainz.org/user/for_emma"));
}
