using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Monochrome search provider against the live-shaped /search/tracks
/// responses, including the configurable host and the unplayable entries.
/// The stream download lives in <see cref="MonochromeDownloaderTests"/>.
/// </summary>
[TestFixture]
public class MonochromeSearchProviderTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Respond(request));
        }
    }

    private static MonochromeSearchProvider Create(
        StubHttpMessageHandler handler, StubPluginConfig? config = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(MonochromeApi.DefaultBaseUrl) };
        return new MonochromeSearchProvider(
            http, config ?? new StubPluginConfig(),
            NullLogger<MonochromeSearchProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    /// <summary>Trimmed real /search/tracks entry (live-probed shape).</summary>
    private static object Track(
        string id, string title, string artist, double durationMs,
        string? isrc = null, bool playable = true) => new
    {
        id,
        trackId = id,
        title,
        artistNames = new[] { artist },
        releaseId = "155143793526444032",
        artwork = "https://tracks.monochrome.st/proxy/mi/155143793526444032-1A01.jpg",
        @explicit = false,
        playable,
        duration = durationMs,
        isrc,
        recordingId = "156636643187494912",
    };

    private static object TracksJson(params object[] items) => new { tracks = items };

    private const string AroundTheWorldId = "155143832910958592";

    private static string QueryOf(HttpRequestMessage request)
        => Uri.UnescapeDataString(request.RequestUri!.Query);

    [Test]
    public async Task SearchAsync_WithAnIsrc_AsksForTheCodeFirst()
    {
        var queries = new List<string>();
        var handler = new StubHttpMessageHandler
        {
            Respond = request =>
            {
                queries.Add(QueryOf(request));
                return Json(HttpStatusCode.OK, TracksJson(
                    Track("222", "The Last Airbender", "SLVL", 217000, isrc: "USRC17607839")));
            },
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome", new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.Multiple(() =>
        {
            Assert.That(queries.First(), Does.Contain("USRC17607839"),
                "the code is the first catalogue query");
            Assert.That(queries, Has.Count.EqualTo(1),
                "the exact code resolved it; the text query must not run");
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl, Does.EndWith("/222"));
        });
    }

    [Test]
    public async Task SearchAsync_IsrcOnlyNoise_FallsBackToText()
    {
        var queries = new List<string>();
        var handler = new StubHttpMessageHandler
        {
            Respond = request =>
            {
                var query = QueryOf(request);
                queries.Add(query);
                return query.Contains("USRC17607839", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, TracksJson(
                        Track("999", "Blue Bird", "Random", 226000, isrc: "OTHER00000001")))
                    : Json(HttpStatusCode.OK, TracksJson(
                        Track("222", "The Last Airbender", "SLVL", 217000, isrc: "USRC17607839")));
            },
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome", new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.Multiple(() =>
        {
            Assert.That(queries, Has.Count.EqualTo(2),
                "the strict code pass is followed by the text pass");
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl, Does.EndWith("/222"));
        });
    }

    [Test]
    public async Task SearchAsync_ParsesTracksShape_AndBuildsStreamUrl()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track(AroundTheWorldId, "Around the World", "Daft Punk", 429533, isrc: "GBDUW0600009"))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl,
                Is.EqualTo($"{MonochromeApi.DefaultBaseUrl}/track/{AroundTheWorldId}"));
            Assert.That(hit.Title, Is.EqualTo("Around the World"));
            Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
            Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(429533)));
            Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        });
    }

    [Test]
    public async Task SearchAsync_RequestsTheTracksEndpoint_WithALimit()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track(AroundTheWorldId, "Around the World", "Daft Punk", 429533))),
        };
        var provider = Create(handler);

        await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(handler.RequestedUrls, Has.Count.EqualTo(1));
        Assert.That(handler.RequestedUrls[0], Does.StartWith(
            MonochromeApi.DefaultBaseUrl + "/search/tracks?q="));
        Assert.That(handler.RequestedUrls[0], Does.Contain("limit=10"));
        Assert.That(handler.RequestedUrls[0], Does.Contain("Daft%20Punk"));
    }

    [Test]
    public async Task SearchAsync_PicksTheRealMatch_NotTheFirstItem()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track("111", "Around the World (Remix)", "Some DJ", 240000),
                Track("222", "Around the World", "Daft Punk", 429000))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo($"{MonochromeApi.DefaultBaseUrl}/track/222"));
        Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
    }

    [Test]
    public async Task SearchAsync_SkipsUnplayableItems()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track("111", "Around the World", "Daft Punk", 429533, playable: false),
                Track("222", "Around the World", "Daft Punk", 429533))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo($"{MonochromeApi.DefaultBaseUrl}/track/222"),
            "the unplayable entry would only fail after a wasted download attempt");
    }

    [Test]
    public async Task SearchAsync_OnlyUnplayable_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track("111", "Around the World", "Daft Punk", 429533, playable: false))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
    }

    [Test]
    public async Task SearchAsync_ExactIsrc_AcceptsADifferentlyWordedTitle()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track("222", "The Last Airbender", "SLVL", 217000, isrc: "USRC17607839"))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome", new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo($"{MonochromeApi.DefaultBaseUrl}/track/222"));
    }

    [Test]
    public async Task SearchAsync_ExactIsrc_WinsOverACloserTitle()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track("111", "Around the World", "Daft Punk", 429533, isrc: "OTHERISRC001"),
                Track("222", "Around the World (Remastered 2007)", "Daft Punk", 430000, isrc: "GBDUW0600009"))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome", new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429), Isrc: "GBDUW0600009"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo($"{MonochromeApi.DefaultBaseUrl}/track/222"),
            "an exact ISRC is the recording itself, not a guess");
    }

    [Test]
    public async Task SearchAsync_WithoutIsrcMatch_UsesTheFullPool()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track("111", "Around the World", "Daft Punk", 429533, isrc: "OTHERISRC001"),
                Track("222", "Around the World (Remastered 2007)", "Daft Punk", 430000, isrc: "GBDUW0600009"))),
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("monochrome", new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any, Isrc: "NOSUCHISRC99"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo($"{MonochromeApi.DefaultBaseUrl}/track/111"),
            "no candidate carries the ISRC, so the best title match wins");
    }

    [Test]
    public async Task SearchAsync_EmptyOrMalformed_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler();
        var provider = Create(handler);

        handler.Respond = _ => Json(HttpStatusCode.OK, new { tracks = Array.Empty<object>() });
        Assert.That(await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any)), Is.Null,
            "an empty result list is a miss");

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        };
        Assert.That(await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any)), Is.Null,
            "a malformed body is a miss, not a crash");

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.That(await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any)), Is.Null,
            "an HTTP error is a miss, not a throw");
    }

    [Test]
    public async Task SearchAsync_UsesConfiguredBaseUrl()
    {
        var config = new StubPluginConfig();
        config.Set("monochrome", MonochromeApi.BaseUrlKey, "https://mirror.example/");
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(HttpStatusCode.OK, TracksJson(
                Track(AroundTheWorldId, "Around the World", "Daft Punk", 429533))),
        };
        var provider = Create(handler, config);

        var hit = await provider.SearchAsync("monochrome",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.Multiple(() =>
        {
            Assert.That(handler.RequestedUrls[0], Does.StartWith("https://mirror.example/search/tracks"),
                "the configured host replaces the official one");
            Assert.That(hit!.SourceUrl, Is.EqualTo($"https://mirror.example/track/{AroundTheWorldId}"));
        });
    }

    [Test]
    public async Task SearchAsync_ForAnotherSource_IsAMissWithoutARequest()
    {
        var handler = new StubHttpMessageHandler();
        var provider = Create(handler);

        var hit = await provider.SearchAsync("jumo",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(handler.RequestedUrls, Is.Empty);
    }
}
