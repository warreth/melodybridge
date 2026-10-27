using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Jumo;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Jumo search provider: ISRC-first terms, the live-shaped response
/// parser, the source identifier and the negative availability report.
/// The download side lives in <see cref="JumoDownloaderTests"/>.
/// </summary>
[TestFixture]
public class JumoSearchProviderTests
{
    private const string SearchJson = """
    {
      "query": "daft punk one more time",
      "albums": { "total": 2, "items": [
        { "id": "0886443927087", "title": "Random Access Memories" }
      ] },
      "tracks": { "total": 3, "items": [
        {
          "id": 9140024, "title": "One More Time", "duration": 320.4, "isrc": "GBDUW0000053",
          "hires": true, "maximum_bit_depth": 24,
          "performer": { "name": "Daft Punk" },
          "album": { "id": "0886443927087", "title": "Random Access Memories", "artist": { "name": "Daft Punk" } }
        },
        {
          "id": 1068442, "title": "One More Time (Remix)", "duration": 320.4, "isrc": null,
          "hires": false, "maximum_bit_depth": 16,
          "artist": { "name": "Some DJ" },
          "album": { "id": "999", "title": "Remixes", "artist": { "name": "Some DJ" } }
        },
        {
          "id": 555, "title": "One More Time (Live)", "duration": 300.0, "isrc": null,
          "hires": false,
          "album": { "title": "No album id" }
        }
      ] }
    }
    """;

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static (JumoSearchProvider Provider, JumoSession Session) Create(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(JumoClient.Origin) };
        var config = new StubPluginConfig();
        var session = new JumoSession(
            new JumoClient(http, NullLogger<JumoClient>.Instance),
            config, NullLogger<JumoSession>.Instance);
        return (new JumoSearchProvider(session, NullLogger<JumoSearchProvider>.Instance), session);
    }

    private static HttpResponseMessage Regions() =>
        Json("""{"regions":[{"code":"CA","available":true}],"default":"CA"}""");

    [Test]
    public async Task Search_WithAnIsrc_AsksForTheCodeFirst_AndReturnsTheAlbumSource()
    {
        var queries = new List<string>();
        var handler = new StubHandler
        {
            Respond = request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains(JumoClient.RegionsPath)) return Regions();
                if (url.Contains(JumoClient.SearchPath))
                {
                    queries.Add(QueryValue(request.RequestUri, "query"));
                    return Json(SearchJson);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };
        var (provider, _) = Create(handler);

        var hit = await provider.SearchAsync("jumo", new TrackQuery(
            "Daft Punk", "One More Time", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(320), Isrc: "GBDUW0000053"));

        Assert.Multiple(() =>
        {
            Assert.That(queries.First(), Is.EqualTo("GBDUW0000053"),
                "the code is the first search term");
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.Title, Is.EqualTo("One More Time"));
            Assert.That(hit.SourceUrl, Is.EqualTo("jumo:0886443927087:9140024"));
        });
    }

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutARequest()
    {
        var handler = new StubHandler();
        var (provider, _) = Create(handler);

        var hit = await provider.SearchAsync("qobuz",
            new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task Search_Http403_ReportsThePluginUnavailable()
    {
        var handler = new StubHandler
        {
            Respond = request => request.RequestUri!.ToString().Contains(JumoClient.RegionsPath)
                ? Regions()
                : Json("{}", HttpStatusCode.Forbidden),
        };
        var (provider, session) = Create(handler);

        var hit = await provider.SearchAsync("jumo",
            new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any));

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.Null);
            Assert.That(session.LastUnavailableReason, Does.Contain("refused"));
            Assert.That(session.IsAvailableAsync().GetAwaiter().GetResult(), Is.False,
                "the refused search must short-circuit the availability probe");
        });
    }

    [Test]
    public void ParseSearchTracks_PicksTheStudioTrack_AndDropsRemixes()
    {
        var query = new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any, TimeSpan.FromSeconds(320));

        var hit = JumoSearchProvider.ParseSearchTracks(
            SearchJson, query.Artist, query.Title, query.ExpectedDuration);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Title, Is.EqualTo("One More Time"));
        Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
        Assert.That(hit.SourceUrl, Is.EqualTo("jumo:0886443927087:9140024"));
        Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(320.4)).Within(TimeSpan.FromMilliseconds(1)));
    }

    [Test]
    public void SearchQueries_PutTheIsrcFirst_ThenTheText()
    {
        var withIsrc = JumoSearchProvider.SearchQueries(new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any, Isrc: "USRC17607839"));
        Assert.Multiple(() =>
        {
            Assert.That(withIsrc[0], Is.EqualTo("USRC17607839"), "the catalogue indexes the code");
            Assert.That(withIsrc[1], Is.EqualTo("SLVL The Last Airbender VIP"),
                "the cleaned text is the fallback");
        });

        var noIsrc = JumoSearchProvider.SearchQueries(
            new TrackQuery("A", "Song", DownloadQuality.Any));
        Assert.That(noIsrc, Is.EqualTo(new[] { "A Song" }),
            "without a code only the text is searched");
    }

    [Test]
    public void ParseSearchTracks_ExactIsrc_AcceptsADifferentlyWordedTitle()
    {
        const string json = """
        {
          "tracks": { "items": [
            { "id": 2, "title": "The Last Airbender", "duration": 217, "isrc": "USRC17607839",
              "performer": { "name": "SLVL" }, "album": { "id": "b" } }
          ] }
        }
        """;

        var hit = JumoSearchProvider.ParseSearchTracks(json, "SLVL", "The Last Airbender (VIP)",
            TimeSpan.FromSeconds(217), "USRC17607839", requireIsrcMatch: true);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("jumo:b:2"));
    }

    [Test]
    public void ParseSearchTracks_IsrcSearchRequiresAnExactMatch()
    {
        const string json = """
        {
          "tracks": { "items": [
            { "id": 1, "title": "The Last Airbender (VIP)", "duration": 217, "isrc": "OTHER00000001",
              "performer": { "name": "SLVL" }, "album": { "id": "a" } }
          ] }
        }
        """;

        Assert.That(JumoSearchProvider.ParseSearchTracks(json, "SLVL", "The Last Airbender (VIP)",
            null, "USRC17607839", requireIsrcMatch: true), Is.Null,
            "an ISRC search without the code in the response resolves nothing");
        Assert.That(JumoSearchProvider.ParseSearchTracks(json, "SLVL", "The Last Airbender (VIP)",
            null, "USRC17607839"), Is.Not.Null,
            "the text pass still ranks what it got");
    }

    [Test]
    public void ParseSearchTracks_IsrcNarrowsToTheExactRecording()
    {
        const string json = """
        {
          "tracks": { "items": [
            { "id": 1, "title": "One More Time", "duration": 320, "isrc": "OTHER00000001",
              "performer": { "name": "Cover Band" }, "album": { "id": "a" } },
            { "id": 2, "title": "One More Time", "duration": 321, "isrc": "GBDUW0000053",
              "performer": { "name": "Daft Punk" }, "album": { "id": "b" } }
          ] }
        }
        """;

        var hit = JumoSearchProvider.ParseSearchTracks(
            json, "Daft Punk", "One More Time", null, "GBDUW0000053");

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("jumo:b:2"), "the exact ISRC recording must win over titles");
    }

    [Test]
    public void ParseSearchTracks_EntriesWithoutAlbumId_AreSkipped()
    {
        const string json = """
        {
          "tracks": { "items": [
            { "id": 555, "title": "One More Time", "duration": 320,
              "performer": { "name": "Daft Punk" }, "album": { "title": "No album id" } }
          ] }
        }
        """;

        Assert.That(JumoSearchProvider.ParseSearchTracks(json, "Daft Punk", "One More Time"),
            Is.Null, "without an album id the source identifier cannot be built");
    }

    [Test]
    public void SourceId_RoundTrips()
    {
        var url = JumoSourceId.Build("0886443927087", "9140024");
        Assert.That(url, Is.EqualTo("jumo:0886443927087:9140024"));

        Assert.That(JumoSourceId.TryParse(url, out var albumId, out var trackId), Is.True);
        Assert.That(albumId, Is.EqualTo("0886443927087"));
        Assert.That(trackId, Is.EqualTo("9140024"));

        Assert.That(JumoSourceId.TryParse("https://jumo-dl.pages.dev/xyz", out _, out _), Is.False);
        Assert.That(JumoSourceId.TryParse("jumo:only-two", out _, out _), Is.False);
        Assert.That(JumoSourceId.TryParse("", out _, out _), Is.False);
    }

    private static string QueryValue(Uri uri, string key)
        => uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2 && part[0] == key)
            .Select(part => Uri.UnescapeDataString(part[1]))
            .FirstOrDefault() ?? "";
}
