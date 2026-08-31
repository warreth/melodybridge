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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond(request));
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
        Assert.That(await downloader.IsAvailableAsync(), Is.False,
            "Lucida behind Cloudflare must not join the waterfall without a solver");
    }

    [Test]
    public async Task SearchAsync_ParsesEmbeddedTracks_AndRanksBestMatch()
    {
        var solver = new StubSolver();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            Assert.That(request.RequestUri!.ToString(), Does.Contain("service=tidal"),
                "search defaults to the Tidal service for high-quality rips");
            Assert.That(request.Headers.Contains("Cookie"), Is.True,
                "the clearance cookie must travel with the request");
            var tracks = new
            {
                tracks = new object[]
                {
                    new { title = "Some Other Song", artists = new[] { new { name = "Someone Else" } }, url = "https://tidal.com/track/1", duration = 3000 },
                    new { title = "Never Gonna Give You Up", artists = new[] { new { name = "Rick Astley" } }, url = "https://tidal.com/track/2", duration = 213000 },
                },
            };
            return Html(",{\"type\":\"data\",\"data\":" + JsonSerializer.Serialize(tracks));
        };

        var downloader = Create(solver, handler, out var http);
        var hit = await downloader.SearchAsync(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/track/2"));
        Assert.That(hit.Title, Is.EqualTo("Never Gonna Give You Up"));
        Assert.That(hit.Artist, Is.EqualTo("Rick Astley"));
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(213)));
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
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("fake-flac-bytes")
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
        Assert.That(content, Is.EqualTo("fake-flac-bytes"));
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
