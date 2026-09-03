using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Cloudflare;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// FlareSolverrSolver auto-detect mode against stubbed HTTP: the sweep
/// must probe the Docker network candidates in order, honor its negative
/// cache, and leave "off" and explicit URLs behaving exactly as before.
/// No live network access needed.
/// </summary>
[TestFixture]
public class FlareSolverrSolverTests
{
    private class StubHttpMessageHandler : HttpMessageHandler
    {
        public List<(string Method, string Url)> Requests { get; } = new();

        /// <summary>Per-URL responses; any URL without an entry throws, like a dead container.</summary>
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method.Method, url));
            if (Routes.TryGetValue(url, out var respond))
                return Task.FromResult(respond());
            throw new HttpRequestException($"no route for {url}");
        }
    }

    private static FlareSolverrSolver Create(StubHttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new FlareSolverrOptions { Url = "auto" }),
            NullLogger<FlareSolverrSolver>.Instance);

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("OK", Encoding.UTF8, "text/plain"),
    };

    private static HttpResponseMessage SolveJson(string cookie, string userAgent) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            status = "ok",
            solution = new { userAgent, cookies = new[] { new { name = "cf_clearance", value = cookie } } },
        }), Encoding.UTF8, "application/json"),
    };

    private static string[] HealthUrls =
    {
        "http://flaresolverr:8191/health",
        "http://host.docker.internal:8191/health",
        "http://127.0.0.1:8191/health",
    };

    [SetUp]
    public void Reset()
    {
        FlareSolverrSolver.Url = "off";
        FlareSolverrSolver.Clock = () => DateTimeOffset.UtcNow;
    }

    [Test]
    public async Task AutoMode_ProbesCandidatesInOrder_AndUsesFirstHealthy()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://host.docker.internal:8191/health"] = Ok;
        var solver = Create(handler);
        FlareSolverrSolver.Url = "auto";

        Assert.That(await solver.IsAvailableAsync(), Is.True);
        Assert.That(handler.Requests.Select(r => r.Url), Is.EqualTo(HealthUrls.Take(2)));
    }

    [Test]
    public async Task AutoMode_SolvePostsToDetectedBaseUrl()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://flaresolverr:8191/health"] = Ok;
        handler.Routes["http://flaresolverr:8191/v1"] = () => SolveJson("clear", "ua");
        var solver = Create(handler);
        FlareSolverrSolver.Url = "auto";

        var result = await solver.SolveAsync("https://lucida.to");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.CookieHeader, Is.EqualTo("cf_clearance=clear"));
        Assert.That(result.UserAgent, Is.EqualTo("ua"));
        Assert.That(handler.Requests.Select(r => r.Url),
            Is.EqualTo(new[] { "http://flaresolverr:8191/health", "http://flaresolverr:8191/v1" }));
    }

    [Test]
    public async Task AutoMode_NegativeCacheSuppressesRepeatedSweeps()
    {
        using var handler = new StubHttpMessageHandler();
        var solver = Create(handler);
        FlareSolverrSolver.Url = "auto";

        // No routes: every /health throws, so the sweep fails.
        Assert.That(await solver.IsAvailableAsync(), Is.False);
        Assert.That(await solver.SolveAsync("https://lucida.to"), Is.Null);
        Assert.That(handler.Requests.Count, Is.EqualTo(3),
            "SolveAsync inside the negative window must not re-probe");

        // Second call inside the window: zero new requests.
        var before = handler.Requests.Count;
        Assert.That(await solver.IsAvailableAsync(), Is.False);
        Assert.That(handler.Requests.Count, Is.EqualTo(before));

        // Once the failed sweep ages out, the candidates are probed again.
        handler.Routes["http://127.0.0.1:8191/health"] = Ok;
        FlareSolverrSolver.Clock = () => DateTimeOffset.UtcNow.AddSeconds(61);
        Assert.That(await solver.IsAvailableAsync(), Is.True);
        Assert.That(handler.Requests.Count, Is.EqualTo(before + 3),
            "an expired negative cache must trigger a fresh sweep");
    }

    [Test]
    public async Task AutoMode_NonHealth200MeansDead()
    {
        using var handler = new StubHttpMessageHandler();
        foreach (var url in HealthUrls)
            handler.Routes[url] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var solver = Create(handler);
        FlareSolverrSolver.Url = "auto";

        Assert.That(await solver.IsAvailableAsync(), Is.False);
        Assert.That(await solver.SolveAsync("https://lucida.to"), Is.Null);
    }

    [Test]
    public void OffMode_IsInstantlyDisabledWithNoRequests()
    {
        using var handler = new StubHttpMessageHandler();
        var solver = Create(handler);
        FlareSolverrSolver.Url = "off";

        Assert.That(FlareSolverrSolver.IsAutoMode, Is.False);
        Assert.That(solver.IsAvailableAsync().IsCompleted, Is.True);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task OffMode_SolveReturnsNullWithoutRequests()
    {
        using var handler = new StubHttpMessageHandler();
        var solver = Create(handler);
        FlareSolverrSolver.Url = "off";

        Assert.That(await solver.SolveAsync("https://lucida.to"), Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ExplicitUrl_SkipsDetectionAndPostsStraight()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://solver.lan:8191/v1"] = () => SolveJson("c2", "ua2");
        var solver = new FlareSolverrSolver(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(
                new FlareSolverrOptions { Url = "http://solver.lan:8191" }),
            NullLogger<FlareSolverrSolver>.Instance);

        Assert.That(FlareSolverrSolver.IsAutoMode, Is.False);
        Assert.That(await solver.IsAvailableAsync(), Is.True);
        var result = await solver.SolveAsync("https://lucida.to");
        Assert.That(result, Is.Not.Null);
        Assert.That(handler.Requests.Select(r => r.Url), Is.EqualTo(new[] { "http://solver.lan:8191/v1" }));
    }

    [Test]
    public async Task EmptyUrl_IsAlsoDisabled()
    {
        using var handler = new StubHttpMessageHandler();
        var solver = new FlareSolverrSolver(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new FlareSolverrOptions { Url = "" }),
            NullLogger<FlareSolverrSolver>.Instance);

        Assert.That(await solver.IsAvailableAsync(), Is.False);
        Assert.That(await solver.SolveAsync("https://lucida.to"), Is.Null);
        Assert.That(handler.Requests, Is.Empty);
    }
}
