using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Monochrome plugin against stubbed HTTP responses following the real
/// Hi-Fi API v2.x shapes (search items array, /track/ manifest chain,
/// mirror fallback). No live network access needed.
/// </summary>
[TestFixture]
public class MonochromeDownloaderTests
{
    private class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(Respond(request));
        }
    }

    private static MonochromeDownloader Create(StubHttpMessageHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler)
        {
            // Instance URLs are absolute; the base just needs to exist.
            BaseAddress = new Uri("https://monochrome.tf"),
        };
        return new MonochromeDownloader(http, NullLogger<MonochromeDownloader>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(data),
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-monochrome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Trimmed real /search/?s= response entry (v2.5 shape, live probe).</summary>
    private static object SearchBody() => new
    {
        version = "2.5",
        data = new
        {
            limit = 25,
            offset = 0,
            totalNumberOfItems = 1,
            items = new object[]
            {
                new
                {
                    id = 12336220,
                    title = "Never Gonna Give You Up",
                    duration = 213,
                    artist = new { name = "Rick Astley" },
                    album = new { title = "Whenever You Need Somebody", cover = "1234" },
                    audioQuality = "LOSSLESS",
                    url = "http://www.tidal.com/track/12336220",
                },
            },
        },
    };

    [Test]
    public async Task SearchAsync_ParsesItemsShape_AndBuildsTidalSourceUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, SearchBody());
        var downloader = Create(handler, out var http);

        var hit = await downloader.SearchAsync(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/12336220"));
            Assert.That(hit.Title, Is.EqualTo("Never Gonna Give You Up"));
            Assert.That(hit.Artist, Is.EqualTo("Rick Astley"));
            Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(213)));
            Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        });
    }

    [Test]
    public async Task SearchAsync_FirstInstance500s_FallsBackToSecondInstance()
    {
        var handler = new StubHttpMessageHandler();
        var answered = "";
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("eu-central.monochrome.tf"))
                return Json(HttpStatusCode.InternalServerError,
                    new { detail = "Upstream API error" });
            if (url.Contains("us-west.monochrome.tf"))
            {
                answered = url;
                return Json(HttpStatusCode.OK, SearchBody());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Create(handler, out var http);

        var hit = await downloader.SearchAsync(
            "Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any);

        Assert.That(hit, Is.Not.Null, "the second instance must take over when the first 500s");
        Assert.That(answered, Does.Contain("/search/?s="),
            "the fallback must still hit the search endpoint");
    }

    [Test]
    public async Task SearchAsync_UsesSParameter_NotLegacyQParameter()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, SearchBody());
        var downloader = Create(handler, out var http);

        await downloader.SearchAsync("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any);

        var searchUrl = handler.RequestedUrls.FirstOrDefault(u => u.Contains("/search/"));
        Assert.That(searchUrl, Is.Not.Null, "a search request must have been made");
        Assert.That(searchUrl, Does.Contain("/search/?s="),
            "the v2.x API answers to s= — q= now returns HTTP 400");
        Assert.That(searchUrl, Does.Not.Contain("?q="),
            "the legacy q= parameter must not be sent anymore");
    }

    [Test]
    public async Task DownloadAsync_ExtractsManifestUrl_DownloadsAndTagsFile()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();
        var manifestUrl = "https://eu-central.monochrome.tf/streams/12336220.flac";
        var trackBody = new
        {
            version = "2.5",
            data = new
            {
                data = new
                {
                    attributes = new { uri = manifestUrl },
                },
            },
        };
        var audio = new byte[] { 0x66, 0x4C, 0x61, 0x43 }; // "fLaC"
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/track/?id="))
                return Json(HttpStatusCode.OK, trackBody);
            if (url == manifestUrl)
                return Bytes(audio);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Create(handler, out var http);

        var result = await downloader.DownloadAsync(
            "https://tidal.com/browse/track/12336220", dir, "melody-test-id",
            new DownloadQuality(AudioFormat.Flac, MinKbps: 400, MaxKbps: 1411));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Does.EndWith(".flac"),
                "the extension must come from the manifest URL");
            Assert.That(result.FilePath, Does.Contain("tidal_12336220_LOSSLESS"),
                "capped FLAC maps to LOSSLESS; the file name is tidal_{id}_{qualityParam}{ext}");
            Assert.That(File.Exists(result.FilePath), Is.True, "the audio bytes must be on disk");
        });
        Assert.That(await File.ReadAllBytesAsync(result.FilePath!), Is.EqualTo(audio),
            "the downloaded bytes must be written verbatim");

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_AllInstancesFail_ReturnsFailureWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.InternalServerError,
            new { detail = "Upstream API error" });
        var downloader = Create(handler, out var http);
        var dir = TempDir();

        var result = await downloader.DownloadAsync(
            "https://tidal.com/browse/track/12336220", dir, "melody-x");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "a full mirror outage must be a failure result");
            Assert.That(result.ErrorMessage, Is.Not.Null);
        });
        Assert.That(Directory.GetFiles(dir), Is.Empty,
            "no partial files may survive a failed download");

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public void TryExtractTrackId_HandlesAllObservedUrlShapes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MonochromeDownloader.TryExtractTrackId(
                "https://tidal.com/browse/track/123", out var a), Is.True);
            Assert.That(a, Is.EqualTo(123));

            Assert.That(MonochromeDownloader.TryExtractTrackId(
                "http://www.tidal.com/track/123", out var b), Is.True);
            Assert.That(b, Is.EqualTo(123));

            Assert.That(MonochromeDownloader.TryExtractTrackId("123", out var c), Is.True);
            Assert.That(c, Is.EqualTo(123));

            Assert.That(MonochromeDownloader.TryExtractTrackId(
                "https://tidal.com/browse/track/not-a-number", out var _), Is.False,
                "non-numeric last segments must not parse as track ids");
            Assert.That(MonochromeDownloader.TryExtractTrackId("", out var _), Is.False);
        });
    }
}
