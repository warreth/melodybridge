using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// DoubleDouble plugin against stubbed HTTP following the real frontend
/// flow (submit /dl?url=, poll /dl/{id}, stream the done URL). No live
/// network access needed.
/// </summary>
[TestFixture]
public class DoubleDoubleDownloaderTests
{
    private class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond(request));
    }

    /// <summary>Delay stub so poll tests run instantly instead of every 2s.</summary>
    private class NoDelayDownloader(HttpClient http) : DoubleDoubleDownloader(
        http, NullLogger<DoubleDoubleDownloader>.Instance)
    {
        internal override Task Delay(int milliseconds, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static DoubleDoubleDownloader Create(
        StubHttpMessageHandler handler, out HttpClient http, int maxPollAttempts = 45)
    {
        http = new HttpClient(handler)
        {
            // The request URLs are absolute; the base just needs to exist.
            BaseAddress = new Uri("https://us.doubledouble.top"),
        };
        return new NoDelayDownloader(http) { MaxPollAttempts = maxPollAttempts };
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(string data) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(data)),
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-dd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task DownloadAsync_SubmitsPollsAndStreamsFile()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();
        var pollCount = 0;

        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/dl?url="))
                return Json(HttpStatusCode.OK, new { success = true, id = "job1" });
            if (url.EndsWith("/dl/job1"))
            {
                pollCount++;
                return pollCount == 1
                    ? Json(HttpStatusCode.OK, new { status = "downloading", percent = 10 })
                    : Json(HttpStatusCode.OK, new { status = "done", url = "https://cdn.example/x.flac" });
            }
            if (url == "https://cdn.example/x.flac")
                return Bytes("fake-flac-bytes");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var downloader = Create(handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://tidal.com/track/12345", dir, "melody-test-id");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Does.EndWith(".flac"),
                "the download URL extension must become the file extension");
            Assert.That(pollCount, Is.EqualTo(2), "downloading then done");
            Assert.That(File.Exists(result.FilePath), Is.True);
        });
        Assert.That(new FileInfo(result.FilePath!).Length,
            Is.EqualTo(Encoding.UTF8.GetByteCount("fake-flac-bytes")),
            "the stub bytes must land on disk intact");
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_ResolvesRelativeUrlAgainstHost()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();

        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/dl?url="))
                return Json(HttpStatusCode.OK, new { success = true, id = "job2" });
            if (url.EndsWith("/dl/job2"))
                return Json(HttpStatusCode.OK,
                    new { status = "done", url = "./dl/abc.flac" });
            if (url == "https://us.doubledouble.top/dl/abc.flac")
                return Bytes("relative");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var downloader = Create(handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://open.qobuz.com/track/99", dir, null);

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(File.ReadAllText(result.FilePath!), Is.EqualTo("relative"));
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_CaptchaError_FailsGracefully()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();

        handler.Respond = _ => Json(HttpStatusCode.OK,
            new { success = false, error = "CAPTCHA is required to continue." });

        var downloader = Create(handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://tidal.com/track/1", dir, "melody-x");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("CAPTCHA is required to continue."),
                "the captcha message must surface so the waterfall can move on");
            Assert.That(Directory.EnumerateFiles(dir), Is.Empty,
                "no file may be written on a rejected submission");
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_PollErrorWithoutKickback_FailsWithMessage()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();

        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/dl?url="))
                return Json(HttpStatusCode.OK, new { success = true, id = "job3" });
            if (url.EndsWith("/dl/job3"))
                return Json(HttpStatusCode.OK,
                    new { status = "error", message = "source unavailable", kickback = false });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var downloader = Create(handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://deezer.com/page/track/1", dir, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("source unavailable"));
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_UsDown_FallsBackToEuHost()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();
        var euPolled = false;

        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://us.doubledouble.top"))
                throw new HttpRequestException("connection refused");
            if (url.StartsWith("https://eu.doubledouble.top") && url.Contains("/dl?url="))
                return Json(HttpStatusCode.OK, new { success = true, id = "eu1" });
            if (url.EndsWith("/dl/eu1"))
            {
                euPolled = true;
                return Json(HttpStatusCode.OK,
                    new { status = "done", url = "https://cdn.example/eu.flac" });
            }
            if (url == "https://cdn.example/eu.flac")
                return Bytes("eu-bytes");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var downloader = Create(handler, out var http);
        var result = await downloader.DownloadAsync(
            "https://soundcloud.com/artist/song", dir, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(euPolled, Is.True, "the eu host must take over when us refuses");
        });
        Assert.That(await File.ReadAllTextAsync(result.FilePath!), Is.EqualTo("eu-bytes"));
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task SearchAsync_ReturnsNull_WithoutHttpCall()
    {
        var handler = new StubHttpMessageHandler { Respond = _ =>
        {
            Assert.Fail("SearchAsync must not touch the network");
            return new HttpResponseMessage(HttpStatusCode.OK);
        } };
        var downloader = Create(handler, out var http);

        Assert.That(await downloader.SearchAsync("Artist", "Title", DownloadQuality.Any), Is.Null,
            "DoubleDouble has no captcha-free metadata search; it only downloads direct URLs");
    }

    [Test]
    public async Task DownloadAsync_PollNeverDone_TimesOut()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();
        var polls = 0;

        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/dl?url="))
                return Json(HttpStatusCode.OK, new { success = true, id = "slow" });
            if (url.EndsWith("/dl/slow"))
            {
                polls++;
                return Json(HttpStatusCode.OK, new { status = "downloading", percent = polls * 5 });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        // Cap of 2 keeps the timeout test instant.
        var downloader = Create(handler, out var http, maxPollAttempts: 2);
        var result = await downloader.DownloadAsync(
            "https://tidal.com/track/7", dir, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("did not finish"),
                "a never-done poll must end in a clear timeout failure");
            Assert.That(polls, Is.EqualTo(2), "exactly MaxPollAttempts polls");
            Assert.That(Directory.EnumerateFiles(dir), Is.Empty);
        });
        Directory.Delete(dir, recursive: true);
    }
}
