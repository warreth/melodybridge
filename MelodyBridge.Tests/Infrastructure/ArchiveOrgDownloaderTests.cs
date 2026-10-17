using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Internet Archive plugin against a stubbed HTTP boundary: the real
/// availability probe, the transient-500 retry and the quality gate, with
/// a valid 128 kbps MP3 as the payload. No live network access needed.
/// </summary>
[TestFixture]
public class ArchiveOrgDownloaderTests
{
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

    private static ArchiveOrgDownloader Create(StubHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri("https://archive.org") };
        return new ArchiveOrgDownloader(http, NullLogger<ArchiveOrgDownloader>.Instance);
    }

    private static HttpResponseMessage Mp3() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(TestAudio.MinimalMp3()),
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-ia-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task IsAvailable_SearchApiAnswers_True()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":{\"numFound\":1}}", Encoding.UTF8, "application/json"),
            },
        };
        var downloader = Create(handler, out var http);

        var available = await downloader.IsAvailableAsync();
        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True);
            Assert.That(downloader.LastUnavailableReason, Is.Null);
            Assert.That(handler.Requests, Has.Count.EqualTo(1), "one cheap probe, not a sweep");
            Assert.That(handler.Requests[0], Does.Contain("advancedsearch.php"));
        });
    }

    [Test]
    public async Task IsAvailable_ArchiveAnswers500_FalseWithTheReason()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        var downloader = Create(handler, out var http);

        var available = await downloader.IsAvailableAsync();
        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False, "a 5xx archive is not usable");
            Assert.That(downloader.LastUnavailableReason, Does.Contain("archive.org"));
        });
    }

    [Test]
    public async Task DownloadAsync_Transient500_IsRetriedAndSucceeds()
    {
        var dir = TempDir();
        var attempts = 0;
        var handler = new StubHandler
        {
            Respond = request =>
            {
                if (!request.RequestUri!.ToString().Contains("/download/"))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                attempts++;
                return attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Mp3();
            },
        };
        var downloader = Create(handler, out var http);

        var result = await downloader.DownloadAsync(
            "https://archive.org/download/testitem/Artist - Title.mp3", dir, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(attempts, Is.EqualTo(2), "the 500 must be retried once, then delivered");
            Assert.That(File.Exists(result.FilePath), Is.True);
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_NotFound_IsNotRetried()
    {
        var dir = TempDir();
        var attempts = 0;
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                attempts++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
        };
        var downloader = Create(handler, out var http);

        var result = await downloader.DownloadAsync(
            "https://archive.org/download/gone/Artist - Title.mp3", dir, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("404"));
            Assert.That(attempts, Is.EqualTo(1), "a 4xx is a verdict, never hammered");
            Assert.That(Directory.EnumerateFiles(dir), Is.Empty);
        });
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_NonArchiveUrl_FailsWithoutNetwork()
    {
        var dir = TempDir();
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                Assert.Fail("a non-archive URL must not be fetched");
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var downloader = Create(handler, out var http);

        var result = await downloader.DownloadAsync("https://example.com/x.mp3", dir, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("archive.org"));
            Assert.That(handler.Requests, Is.Empty);
        });
        Directory.Delete(dir, recursive: true);
    }
}
