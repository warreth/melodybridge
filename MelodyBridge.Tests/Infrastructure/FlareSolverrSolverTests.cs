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

    /// <summary>The shape current FlareSolverr returns: solution.status is
    /// the HTTP status as a number, which used to break deserialization and
    /// silently disable the Lucida plugin.</summary>
    private static HttpResponseMessage SolveJsonWithNumericStatus(string cookie, string userAgent) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            status = "ok",
            message = "Challenge solved!",
            solution = new
            {
                url = "https://lucida.to",
                status = 200,
                headers = new { },
                response = "<html></html>",
                userAgent,
                cookies = new[] { new { name = "cf_clearance", value = cookie } },
            },
        }), Encoding.UTF8, "application/json"),
    };

    private static string[] HealthUrls =
    {
        "http://flaresolverr:8191/health",
        "http://mb_flaresolverr:8191/health",
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
        Assert.That(handler.Requests.Select(r => r.Url), Is.EqualTo(HealthUrls.Take(3)));
    }

    [Test]
    public async Task AutoMode_FindsTheShippedContainerName()
    {
        // The compose ships container_name mb_flaresolverr; the service
        // name alias can be missing in some runtimes, so the sweep must
        // check both identities.
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://mb_flaresolverr:8191/health"] = Ok;
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
        Assert.That(handler.Requests.Count, Is.EqualTo(4),
            "SolveAsync inside the negative window must not re-probe");

        // Second call inside the window: zero new requests.
        var before = handler.Requests.Count;
        Assert.That(await solver.IsAvailableAsync(), Is.False);
        Assert.That(handler.Requests.Count, Is.EqualTo(before));

        // Once the failed sweep ages out, the candidates are probed again.
        handler.Routes["http://127.0.0.1:8191/health"] = Ok;
        FlareSolverrSolver.Clock = () => DateTimeOffset.UtcNow.AddSeconds(61);
        Assert.That(await solver.IsAvailableAsync(), Is.True);
        Assert.That(handler.Requests.Count, Is.EqualTo(before + 4),
            "an expired negative cache must trigger a fresh sweep");
    }

    [Test]
    public async Task Solve_WithNumericSolutionStatus_StillReturnsCredentials()
    {
        // The exact response shape that produced "The JSON value could not
        // be converted to ...FlareSolverrSolution. Path: $.solution.status"
        // in the app log.
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://solver.lan:8191/v1"] = () => SolveJsonWithNumericStatus("num", "ua-num");
        var solver = new FlareSolverrSolver(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(
                new FlareSolverrOptions { Url = "http://solver.lan:8191" }),
            NullLogger<FlareSolverrSolver>.Instance);

        var result = await solver.SolveAsync("https://lucida.to");

        Assert.That(result, Is.Not.Null, "a numeric solution.status must not fail the solve");
        Assert.That(result!.CookieHeader, Is.EqualTo("cf_clearance=num"));
        Assert.That(result.UserAgent, Is.EqualTo("ua-num"));
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
    public async Task ExplicitUrl_ChecksHealthOnceAndPostsStraight()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://solver.lan:8191/health"] = Ok;
        handler.Routes["http://solver.lan:8191/v1"] = () => SolveJson("c2", "ua2");
        var solver = new FlareSolverrSolver(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(
                new FlareSolverrOptions { Url = "http://solver.lan:8191" }),
            NullLogger<FlareSolverrSolver>.Instance);

        Assert.That(FlareSolverrSolver.IsAutoMode, Is.False);
        Assert.That(await solver.IsAvailableAsync(), Is.True);
        Assert.That(await solver.IsAvailableAsync(), Is.True, "the health answer is cached briefly");
        var result = await solver.SolveAsync("https://lucida.to");
        Assert.That(result, Is.Not.Null);
        Assert.That(handler.Requests.Select(r => r.Url),
            Is.EqualTo(new[] { "http://solver.lan:8191/health", "http://solver.lan:8191/v1" }));
    }

    [Test]
    public async Task ExplicitUrl_UnhealthyEndpointIsNotReady()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://solver.lan:8191/health"] = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        var solver = new FlareSolverrSolver(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(
                new FlareSolverrOptions { Url = "http://solver.lan:8191" }),
            NullLogger<FlareSolverrSolver>.Instance);

        Assert.That(await solver.IsAvailableAsync(), Is.False,
            "a wrong explicit URL must not look ready until the first track fails");
    }

    [Test]
    public async Task Probe_SolvesForReal_AndReportsTheEndpoint()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://flaresolverr:8191/health"] = Ok;
        handler.Routes["http://flaresolverr:8191/v1"] = () => SolveJson("probe", "ua-probe");

        var result = await FlareSolverrSolver.ProbeAsync(
            new HttpClient(handler), "auto", "https://lucida.to");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(result.Endpoint, Is.EqualTo("http://flaresolverr:8191"));
            Assert.That(result.Message, Does.Contain("Solved lucida.to"));
        });
    }

    [Test]
    public async Task Probe_Off_IsAnHonestFailureWithoutRequests()
    {
        using var handler = new StubHttpMessageHandler();
        var result = await FlareSolverrSolver.ProbeAsync(
            new HttpClient(handler), "off", "https://lucida.to");

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Message, Does.Contain("off"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task SolvedClearance_IsReusedUntilFreshIsRequested()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Routes["http://solver.lan:8191/v1"] = () => SolveJson("c3", "ua3");
        var solver = new FlareSolverrSolver(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(
                new FlareSolverrOptions { Url = "http://solver.lan:8191" }),
            NullLogger<FlareSolverrSolver>.Instance);

        var first = await solver.SolveAsync("https://lucida.to");
        var second = await solver.SolveAsync("https://lucida.to");
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
        Assert.That(handler.Requests.Count, Is.EqualTo(1),
            "a solved clearance must not open the browser again for every track");

        var fresh = await solver.SolveFreshAsync("https://lucida.to");
        Assert.That(fresh, Is.Not.Null);
        Assert.That(handler.Requests.Count, Is.EqualTo(2),
            "a 403 must be able to force exactly one fresh solve");
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
