using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The lucida.to search provider against a stubbed challenge solver and the
/// real page-data shapes: the service chain, ISRC first, the text fallback
/// and the early stop on a high-confidence hit. The download flow lives in
/// <see cref="LucidaDownloaderTests"/>.
/// </summary>
[TestFixture]
public class LucidaSearchProviderTests
{
    private sealed class StubSolver : IChallengeSolver
    {
        public bool Available { get; set; } = true;
        public int SolveCalls { get; private set; }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(Available);
        public Task<CloudflareCredentials?> SolveAsync(string url, CancellationToken ct = default)
        {
            SolveCalls++;
            return Task.FromResult<CloudflareCredentials?>(
                new CloudflareCredentials("cf_clearance=abc123", "TestUA/1.0"));
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(Respond(request));
        }
    }

    private static LucidaSearchProvider Create(
        StubSolver solver, StubHttpMessageHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri(LucidaSession.BaseUrl) };
        var session = new LucidaSession(
            http, solver, NullLogger<LucidaSession>.Instance);
        return new LucidaSearchProvider(session, NullLogger<LucidaSearchProvider>.Instance);
    }

    private static HttpResponseMessage Html(string pageData) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "<html><head></head><body><script>Array.prototype.something" + pageData +
            ",\"uses\":{\"url\":1}}];</script></body></html>"),
    };

    [Test]
    public async Task SearchAsync_ParsesEmbeddedTracks_AndRanksBestMatch()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            Assert.That(request.RequestUri!.ToString(), Does.Contain("service=qobuz"),
                "the chain starts with Qobuz, which currently resolves through NL");
            Assert.That(request.RequestUri!.ToString(), Does.Contain("country=NL"),
                "Qobuz searches its working Netherlands storefront");
            Assert.That(request.Headers.Contains("Cookie"), Is.True,
                "the clearance cookie must travel with the request");
            var tracks = new
            {
                tracks = new object[]
                {
                    new { title = "Some Other Song", artists = new[] { new { name = "Someone Else" } }, url = "https://qobuz.com/track/1", duration = 3000 },
                    new { title = "Never Gonna Give You Up", artists = new[] { new { name = "Rick Astley" } }, url = "https://qobuz.com/track/2", duration = 213000 },
                },
            };
            return Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(tracks));
        };

        var provider = Create(solver, handler, out _);
        var hit = await provider.SearchAsync("lucida",
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://qobuz.com/track/2"));
        Assert.That(hit.Title, Is.EqualTo("Never Gonna Give You Up"));
        Assert.That(hit.Artist, Is.EqualTo("Rick Astley"));
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(213)));
    }

    [Test]
    public async Task SearchAsync_WithIsrc_SearchesByTheCodeFirst()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            Assert.That(request.RequestUri!.ToString(), Does.Contain("query=GBARL9300135"),
                "the ISRC goes in as the search query");
            var tracks = new
            {
                tracks = new object[]
                {
                    new { title = "Never Gonna Give You Up", artists = new[] { new { name = "Rick Astley" } }, url = "https://qobuz.com/track/2", duration = 213000 },
                },
            };
            return Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(tracks));
        };

        var provider = Create(solver, handler, out _);
        var hit = await provider.SearchAsync("lucida", new TrackQuery(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any, Isrc: "GBARL9300135"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(handler.Requests, Is.EqualTo(1),
            "a High-confidence ISRC hit stops the chain before any text query");
    }

    [Test]
    public async Task SearchAsync_IsrcOnlyNoise_FallsBackToText()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("query=GBARL9300135"))
            {
                var noise = new
                {
                    tracks = new object[]
                    {
                        new { title = "Blue Bird", artists = new[] { new { name = "Random" } }, url = "https://qobuz.com/track/9", duration = 226000 },
                    },
                };
                return Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(noise));
            }

            var real = new
            {
                tracks = new object[]
                {
                    new { title = "Never Gonna Give You Up", artists = new[] { new { name = "Rick Astley" } }, url = "https://qobuz.com/track/2", duration = 213000 },
                },
            };
            return Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(real));
        };

        var provider = Create(solver, handler, out _);
        var hit = await provider.SearchAsync("lucida", new TrackQuery(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any, Isrc: "GBARL9300135"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://qobuz.com/track/2"));
        Assert.That(handler.Requests, Is.GreaterThanOrEqualTo(5),
            "the ISRC pass is followed by the text pass");
    }

    [Test]
    public async Task SearchAsync_LowConfidenceOnQobuz_FallsThroughToDeezer()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        var noise = new
        {
            tracks = new object[]
            {
                new { title = "Totally Unrelated", artists = new[] { new { name = "Unknown Artist" } }, url = "https://qobuz.com/track/9", duration = 9999 },
            },
        };
        var deezerTracks = new
        {
            tracks = new object[]
            {
                new { title = "Never Gonna Give You Up", artists = new[] { new { name = "Rick Astley" } }, url = "https://deezer.com/track/42", duration = 213000 },
            },
        };
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            return url.Contains("service=deezer")
                ? Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(deezerTracks))
                : Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(noise));
        };

        var provider = Create(solver, handler, out _);
        var hit = await provider.SearchAsync("lucida",
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null, "the deezer fallback must rescue the search");
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://deezer.com/track/42"));
        Assert.That(handler.Requests, Is.EqualTo(3),
            "qobuz, amazon then deezer: exactly three search requests");
    }

    [Test]
    public async Task SearchAsync_HighConfidenceOnFirstService_StopsEarly()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        var tracks = new
        {
            tracks = new object[]
            {
                new { title = "Never Gonna Give You Up", artists = new[] { new { name = "Rick Astley" } }, url = "https://qobuz.com/track/2", duration = 213000 },
            },
        };
        handler.Respond = _ =>
            Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(tracks));

        var provider = Create(solver, handler, out _);
        var hit = await provider.SearchAsync("lucida",
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(handler.Requests, Is.EqualTo(1),
            "a High-confidence hit on Qobuz must not try the other services");
    }

    [Test]
    public async Task SearchAsync_AllServicesGarbage_ReturnsNullWithoutThrowing()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>no page data here</html>"),
            },
        };

        var provider = Create(solver, handler, out _);
        DownloaderSearchHit? hit = null;
        Assert.DoesNotThrowAsync(async () =>
            hit = await provider.SearchAsync("lucida",
                new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any)));

        Assert.That(hit, Is.Null, "garbage responses must yield a null hit, not a crash");
        Assert.That(handler.Requests, Is.EqualTo(4),
            "every service is tried before giving up");
    }

    [Test]
    public async Task SearchAsync_ForAnotherSource_IsAMissWithoutASolve()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        var provider = Create(solver, handler, out _);

        var hit = await provider.SearchAsync("qobuz",
            new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(solver.SolveCalls, Is.Zero);
        Assert.That(handler.Requests, Is.Zero);
    }
}
