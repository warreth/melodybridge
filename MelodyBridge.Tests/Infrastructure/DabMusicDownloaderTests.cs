using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// DAB Music plugin (dabmusic.xyz) against stubbed HTTP responses
/// following the public OpenAPI shapes: /api/search returns a tracks
/// array, /api/stream returns the media URL. No live network access
/// needed; the community service is regularly offline.
/// </summary>
[TestFixture]
public class DabMusicDownloaderTests
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

    private static DabMusicDownloader Create(StubHttpMessageHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler)
        {
            // Request URLs are absolute; the base just needs to exist.
            BaseAddress = new Uri("https://dabmusic.xyz"),
        };
        return new DabMusicDownloader(http, NullLogger<DabMusicDownloader>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage RawJson(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Real audio bytes: the downloader sniffs before accepting.</summary>
    private static HttpResponseMessage FlacBytes(string payload)
    {
        var bytes = new List<byte>();
        bytes.AddRange("fLaC"u8.ToArray());
        bytes.AddRange(Encoding.UTF8.GetBytes(payload));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-dab-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static object SearchBody() => new
    {
        tracks = new object[]
        {
            new
            {
                id = "12345",
                title = "Never Gonna Give You Up",
                artist = "Rick Astley",
                albumTitle = "Whenever You Need Somebody",
                albumCover = "https://cdn.example/cover.jpg",
                duration = 213,
                audioQuality = new { maximumBitDepth = 24, maximumSamplingRate = 96.0, isHiRes = true },
            },
        },
    };

    [Test]
    public async Task SearchAsync_ParsesTracks_AndBuildsTheStreamSourceUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, SearchBody());
        var downloader = Create(handler, out _);

        var hit = await downloader.SearchAsync("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.Title, Is.EqualTo("Never Gonna Give You Up"));
        Assert.That(hit.Artist, Is.EqualTo("Rick Astley"));
        Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(213)));
        Assert.That(hit.SourceUrl, Does.Contain("/api/stream?trackId=12345"));
        Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        Assert.That(handler.RequestedUrls[0], Does.Contain("/api/search?q="),
            "the search uses the documented endpoint");
    }

    [Test]
    public async Task SearchAsync_RanksTheRealMatch_NotTheFirstItem()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, new
        {
            tracks = new object[]
            {
                new { id = 1, title = "Never Gonna Give You Up (Karaoke Version)", artist = "Karaoke Channel", duration = 213 },
                new { id = 2, title = "Never Gonna Give You Up", artist = "Rick Astley", duration = 213 },
            },
        });
        var downloader = Create(handler, out _);

        var hit = await downloader.SearchAsync("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Does.Contain("trackId=2"),
            "the karaoke version is dropped, the real match wins");
    }

    [Test]
    public async Task SearchAsync_NumericIds_AreAccepted()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => RawJson(HttpStatusCode.OK,
            "{\"tracks\":[{\"id\":98765,\"title\":\"Song\",\"artist\":\"Artist\",\"duration\":180}]}");
        var downloader = Create(handler, out _);

        var hit = await downloader.SearchAsync("Artist", "Song", DownloadQuality.Any);

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Does.Contain("trackId=98765"));
    }

    [Test]
    public async Task SearchAsync_EmptyOrFailing_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, new { tracks = Array.Empty<object>() });
        var downloader = Create(handler, out _);
        Assert.That(await downloader.SearchAsync("A", "T", DownloadQuality.Any), Is.Null);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.That(await downloader.SearchAsync("A", "T", DownloadQuality.Any), Is.Null,
            "a down service is a miss, not a crash");
    }

    [Test]
    public async Task DownloadAsync_ResolvesTheStreamAndWritesAudio()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/stream"))
                return Json(HttpStatusCode.OK, new { url = "https://cdn.example/media/track.flac" });
            if (url == "https://cdn.example/media/track.flac")
                return FlacBytes("dab-payload");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Create(handler, out _);

        var result = await downloader.DownloadAsync(
            "https://dabmusic.xyz/api/stream?trackId=12345", dir, "melody-dab-1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Does.EndWith(".flac"));
            Assert.That(Path.GetFileName(result.FilePath!), Does.StartWith("dab_12345_27_melody-dab-1"),
                "the tier and melody id make retries and parallel downloads distinct");
            Assert.That(File.Exists(result.FilePath), Is.True);
        });
        Assert.That(await File.ReadAllTextAsync(result.FilePath!), Is.EqualTo("fLaCdab-payload"),
            "the stubbed bytes land on disk intact");
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_NoTierFits_FailsFastWithoutRequests()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ =>
            {
                Assert.Fail("no HTTP request may run when no tier fits");
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var downloader = Create(handler, out _);

        var result = await downloader.DownloadAsync(
            "https://dabmusic.xyz/api/stream?trackId=1", TempDir(), "melody-x",
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("no DAB Music tier fits"));
        Assert.That(handler.RequestedUrls, Is.Empty);
    }

    [Test]
    public async Task DownloadAsync_NonAudioResponse_IsRejectedAndDeleted()
    {
        var handler = new StubHttpMessageHandler();
        var dir = TempDir();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/stream"))
                return Json(HttpStatusCode.OK, new { url = "https://cdn.example/error" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>challenge</html>"),
            };
        };
        var downloader = Create(handler, out _);

        var result = await downloader.DownloadAsync(
            "https://dabmusic.xyz/api/stream?trackId=7", dir, "melody-dab-2");

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("not audio"));
        Assert.That(Directory.EnumerateFiles(dir), Is.Empty, "the error page must not survive");
    }

    [TestCase("https://dabmusic.xyz/api/stream?trackId=42", "42")]
    [TestCase("https://dabmusic.xyz/api/stream?trackId=42&quality=5", "42")]
    [TestCase("https://dabmusic.xyz/stream/42", "42")]
    [TestCase("42", "42")]
    [TestCase("", null)]
    public void ExtractTrackId_HandlesTheObservedShapes(string url, string? expected)
        => Assert.That(DabMusicDownloader.ExtractTrackId(url), Is.EqualTo(expected));

    [TestCase("{\"url\":\"https://cdn/x.flac\"}", "https://cdn/x.flac")]
    [TestCase("{\"streamUrl\":\"https://cdn/x.flac\"}", "https://cdn/x.flac")]
    [TestCase("{\"data\":{\"url\":\"https://cdn/x.flac\"}}", "https://cdn/x.flac")]
    [TestCase("{\"error\":\"nope\"}", null)]
    [TestCase("not json", null)]
    public void ParseStreamUrl_AcceptsTheObservedShapes(string json, string? expected)
        => Assert.That(DabMusicDownloader.ParseStreamUrl(json), Is.EqualTo(expected));

    [Test]
    public void TryMapQuality_HonorsTheTiers()
    {
        // FLAC requests take the hi-res tier; MP3 requests the 320 tier.
        Assert.That(DabMusicDownloader.TryMapQuality(new DownloadQuality(AudioFormat.Flac)), Is.EqualTo("27"));
        Assert.That(DabMusicDownloader.TryMapQuality(new DownloadQuality(AudioFormat.Mp3)), Is.EqualTo("5"));
        Assert.That(DabMusicDownloader.TryMapQuality(new DownloadQuality(AudioFormat.Aac)), Is.Null,
            "DAB serves no AAC tier; it must not claim one");

        // Auto: a 320-fitting band takes MP3 320, an uncapped one hi-res.
        Assert.That(DabMusicDownloader.TryMapQuality(DownloadQuality.Any), Is.EqualTo("27"));
        Assert.That(DabMusicDownloader.TryMapQuality(
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 320)), Is.EqualTo("5"));
        Assert.That(DabMusicDownloader.TryMapQuality(
            new DownloadQuality(AudioFormat.Auto, MinKbps: 192, MaxKbps: 320)), Is.EqualTo("5"));

        // A lossless floor needs a FLAC tier.
        Assert.That(DabMusicDownloader.TryMapQuality(
            new DownloadQuality(AudioFormat.Auto, MinKbps: 400, MaxKbps: null)), Is.EqualTo("27"));
        Assert.That(DabMusicDownloader.TryMapQuality(
            new DownloadQuality(AudioFormat.Auto, MinKbps: 400, MaxKbps: 1200)), Is.EqualTo("6"));

        // No tier fits a small-file cap or a mid-band lossless floor.
        Assert.That(DabMusicDownloader.TryMapQuality(
            new DownloadQuality(AudioFormat.Auto, MinKbps: null, MaxKbps: 160)), Is.Null);
        Assert.That(DabMusicDownloader.TryMapQuality(
            new DownloadQuality(AudioFormat.Auto, MinKbps: 400, MaxKbps: 500)), Is.Null);
    }

    [Test]
    public async Task IsAvailable_CachesTheVerdict_WithinTheTtl()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(HttpStatusCode.OK, SearchBody());
        var downloader = Create(handler, out _);

        Assert.That(await downloader.IsAvailableAsync(), Is.True);
        var afterFirst = handler.RequestedUrls.Count;
        Assert.That(afterFirst, Is.EqualTo(1), "one cheap probe");

        Assert.That(await downloader.IsAvailableAsync(), Is.True);
        Assert.That(handler.RequestedUrls.Count, Is.EqualTo(afterFirst),
            "a second check within the TTL reuses the cached verdict");
    }
}
