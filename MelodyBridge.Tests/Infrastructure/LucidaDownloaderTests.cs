using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Lucida;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Lucida plugin against a stubbed challenge solver and stubbed HTTP
/// responses following the real client flow (search blob, /api/load
/// handoff, poll, download). No live network access needed.
/// </summary>
[TestFixture]
public class LucidaDownloaderTests
{
    private class StubSolver : IChallengeSolver
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

    private class StubHttpMessageHandler : HttpMessageHandler
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

    private static LucidaDownloader Create(
        StubSolver solver, StubHttpMessageHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler)
        {
            // The request URLs are absolute; the base just needs to exist.
            BaseAddress = new Uri("https://lucida.to"),
        };
        return new LucidaDownloader(http, solver, NullLogger<LucidaDownloader>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Html(string pageData) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "<html><head></head><body><script>Array.prototype.something" + pageData +
            ",\"uses\":{\"url\":1}}];</script></body></html>"),
    };

    [Test]
    public async Task IsAvailable_WithoutSolver_ReturnsFalse()
    {
        var downloader = Create(new StubSolver { Available = false },
            new StubHttpMessageHandler(), out var http);
        var available = await downloader.IsAvailableAsync();
        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False,
                "Lucida behind Cloudflare must not join the waterfall without a solver");
            Assert.That(downloader.LastUnavailableReason, Does.Contain("FlareSolverr"),
                "the card must say what to configure, not just fail");
        });
    }

    [Test]
    public async Task IsAvailable_WithSolver_ReportsReadyAndClearsTheReason()
    {
        var downloader = Create(new StubSolver(), new StubHttpMessageHandler(), out var http);
        var available = await downloader.IsAvailableAsync();
        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True);
            Assert.That(downloader.LastUnavailableReason, Is.Null);
        });
    }

    [Test]
    public async Task DownloadAsync_FollowsHandoffFlow()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-lucida-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var stage = 0;
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/load"))
            {
                stage = 1;
                return Json(HttpStatusCode.OK, new { handoff = "h1", server = "eu2" });
            }
            if (url.EndsWith("/api/fetch/request/h1"))
            {
                stage = 2;
                return Json(HttpStatusCode.OK, new { status = "completed" });
            }
            if (url.EndsWith("/api/fetch/request/h1/download"))
            {
                stage = 3;
                var payload = new List<byte>();
                payload.AddRange("fLaC"u8.ToArray());
                payload.AddRange(System.Text.Encoding.UTF8.GetBytes("fake-flac-bytes"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload.ToArray())
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/flac") },
                    },
                };
            }
            // Track page.
            return Html(",{\"type\":\"data\",\"data\":{\"info\":{\"url\":\"https://tidal.com/track/2\"},\"token\":\"csrf-1\",\"csrfFallback\":null}");
        };

        var downloader = Create(solver, handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://lucida.to/track/tidal/2", dir, "melody-test-id");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Does.EndWith(".flac"),
                "the FLAC content type must map to a .flac file");
            Assert.That(stage, Is.EqualTo(3), "all three stages must run: load, poll, download");
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(solver.SolveCalls, Is.GreaterThanOrEqualTo(1),
                "the solver is consulted before talking to lucida");
        });

        var content = await File.ReadAllTextAsync(result.FilePath!);
        Assert.That(content, Is.EqualTo("fLaCfake-flac-bytes"));
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_DecodesTheDoubleBase64TokenAndUsesTheSourceCountry()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-lucida-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string? loadBody = null;

        string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var token = Encode(Encode("csrf-real"));

        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/load"))
            {
                loadBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, new { error = "test-stop" });
            }

            return Html(",{\"type\":\"data\",\"data\":{\"info\":{\"url\":\"https://qobuz.com/track/2\"},\"token\":\""
                + token + "\",\"csrfFallback\":null}");
        };

        var downloader = Create(solver, handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://qobuz.com/track/2", dir, "melody-token-test");

        Assert.That(result.Success, Is.False);
        Assert.That(loadBody, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(loadBody, Does.Contain("\"primary\":\"csrf-real\""),
                "the site's two atob() calls must be mirrored, or every load is rejected");
            Assert.That(loadBody, Does.Contain("\"id\":\"NL\""),
                "Qobuz currently resolves through the Netherlands account");
            Assert.That(loadBody, Does.Contain("\"url\":\"https://qobuz.com/track/2\""),
                "the canonical page-data URL is submitted, not our lucida wrapper URL");
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_WorkerError_IsReported()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/load"))
                return Json(HttpStatusCode.OK, new { handoff = "h1", server = "eu2" });
            if (url.EndsWith("/api/fetch/request/h1"))
                return Json(HttpStatusCode.OK,
                    new { status = "error", message = "source unavailable" });
            return Html(",{\"type\":\"data\",\"data\":{\"token\":\"csrf-1\",\"csrfFallback\":null}");
        };

        var downloader = Create(solver, handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://lucida.to/track/tidal/2", Path.GetTempPath(), "melody-x");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("source unavailable"));
    }
}
