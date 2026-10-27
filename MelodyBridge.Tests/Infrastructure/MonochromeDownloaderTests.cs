using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Monochrome plugin against stubbed HTTP responses following the real
/// tracks.monochrome.st shapes (tracks array, direct FLAC stream, health
/// probe) plus the configurable base URL. No live network access needed.
/// </summary>
[TestFixture]
public class MonochromeDownloaderTests
{
    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-monochrome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

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

    private static MonochromeDownloader Create(
        StubHttpMessageHandler handler, StubPluginConfig? config = null)
    {
        var http = new HttpClient(handler)
        {
            // Every request URL is absolute; the base only needs to exist.
            BaseAddress = new Uri(MonochromeDownloader.DefaultBaseUrl),
        };
        return new MonochromeDownloader(
            http, config ?? new StubPluginConfig(), NullLogger<MonochromeDownloader>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(data),
    };

    private const string AroundTheWorldId = "155143832910958592";

    private static byte[] FlacBytes(int size = 4096)
    {
        var bytes = new byte[size];
        "fLaC"u8.CopyTo(bytes);
        for (var i = 4; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static byte[] Mp3Bytes(int size = 2048)
    {
        var bytes = new byte[size];
        "ID3"u8.CopyTo(bytes);
        return bytes;
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task DownloadAsync_StreamsTheFlac_IntoATaggedFile()
    {
        var bytes = FlacBytes();
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Bytes(bytes);
        var downloader = Create(handler);
        var dir = NewDir();

        var result = await downloader.DownloadAsync(
            $"{MonochromeDownloader.DefaultBaseUrl}/track/{AroundTheWorldId}", dir, "mb-monochrome-1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(File.Exists(result.FilePath), Is.True, "the reported file exists");
            Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".flac"));
            Assert.That(Path.GetFileName(result.FilePath), Does.Contain("mb-monochrome-1"),
                "the melody id keeps retries and parallel downloads distinct");
            Assert.That(File.ReadAllBytes(result.FilePath!), Is.EqualTo(bytes),
                "the streamed bytes are exactly what the API sent");
        });
        Assert.That(handler.RequestedUrls[0],
            Is.EqualTo($"{MonochromeDownloader.DefaultBaseUrl}/track/{AroundTheWorldId}"));
    }

    [Test]
    public async Task DownloadAsync_SniffedContainer_FixesTheExtension()
    {
        // The API answers with the audio bytes only; a lossy master arrives
        // as MP3 under the .flac guess and must land as .mp3.
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Bytes(Mp3Bytes());
        var downloader = Create(handler);
        var dir = NewDir();

        var result = await downloader.DownloadAsync(AroundTheWorldId, dir, "mb-monochrome-2");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".mp3"));
        Assert.That(Directory.GetFiles(dir), Has.Length.EqualTo(1), "no orphaned .flac file");
    }

    [Test]
    public async Task DownloadAsync_AcceptsBareIdAndTrackUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Bytes(FlacBytes());
        var downloader = Create(handler);
        var dir = NewDir();

        var bare = await downloader.DownloadAsync(AroundTheWorldId, dir, "mb-bare");
        var url = await downloader.DownloadAsync(
            $"{MonochromeDownloader.DefaultBaseUrl}/track/{AroundTheWorldId}/", dir, "mb-url");

        Assert.Multiple(() =>
        {
            Assert.That(bare.Success, Is.True, bare.ErrorMessage);
            Assert.That(url.Success, Is.True, url.ErrorMessage);
        });
    }

    /// <summary>A response body that yields a little data and then cuts off.</summary>
    private sealed class TruncatedStream : Stream
    {
        private int _remaining = 128;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) throw new IOException("the response ended prematurely");
            var read = Math.Min(count, _remaining);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public async Task DownloadAsync_RetriesATruncatedStream()
    {
        var handler = new StubHttpMessageHandler();
        var calls = 0;
        handler.Respond = _ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new TruncatedStream()),
                }
                : Bytes(FlacBytes());
        };
        var downloader = Create(handler);

        var result = await downloader.DownloadAsync(
            $"{MonochromeDownloader.DefaultBaseUrl}/track/{AroundTheWorldId}",
            NewDir(), "mb-monochrome-retry");

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(calls, Is.EqualTo(2), "the cut-off stream is fetched once more");
        Assert.That(File.ReadAllBytes(result.FilePath!), Is.EqualTo(FlacBytes()),
            "the second, complete stream is the file that lands");
    }

    [Test]
    public async Task DownloadAsync_NonAudioBody_DeletesTheFile()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>error page</html>", Encoding.UTF8, "text/html"),
        };
        var downloader = Create(handler);
        var dir = NewDir();

        var result = await downloader.DownloadAsync(AroundTheWorldId, dir, "mb-bad");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not audio"));
            Assert.That(Directory.GetFiles(dir), Is.Empty, "an error page must never be left behind");
        });
    }

    [Test]
    public async Task DownloadAsync_EmptyBody_Fails()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Bytes(Array.Empty<byte>());
        var downloader = Create(handler);
        var dir = NewDir();

        var result = await downloader.DownloadAsync(AroundTheWorldId, dir, "mb-empty");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(Directory.GetFiles(dir), Is.Empty, "a zero-byte rip is a failed rip");
        });
    }

    [Test]
    public async Task DownloadAsync_HttpError_FailsWithoutAFile()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var downloader = Create(handler);
        var dir = NewDir();

        var result = await downloader.DownloadAsync(AroundTheWorldId, dir, "mb-404");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("404"));
            Assert.That(Directory.GetFiles(dir), Is.Empty);
        });
    }

    [Test]
    public async Task DownloadAsync_InvalidSource_Fails()
    {
        var handler = new StubHttpMessageHandler();
        var downloader = Create(handler);
        var dir = NewDir();

        var result = await downloader.DownloadAsync("https://example.com/song.mp3", dir, "mb-nope");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not a Monochrome track URL"));
            Assert.That(handler.RequestedUrls, Is.Empty, "nothing is sent for a foreign URL");
        });
    }

    // ── Availability ─────────────────────────────────────────────────────

    [Test]
    public async Task IsAvailableAsync_HealthOk_TrueAndCached()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        var downloader = Create(handler);

        Assert.That(await downloader.IsAvailableAsync(), Is.True);
        Assert.That(await downloader.IsAvailableAsync(), Is.True);
        Assert.That(handler.RequestedUrls, Has.Count.EqualTo(1), "the verdict is cached");
        Assert.That(handler.RequestedUrls[0],
            Is.EqualTo(MonochromeDownloader.DefaultBaseUrl + "/health"));
    }

    [Test]
    public async Task IsAvailableAsync_HealthFailure_False()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        var downloader = Create(handler);

        Assert.That(await downloader.IsAvailableAsync(), Is.False);
    }

    // ── Id extraction ────────────────────────────────────────────────────

    [TestCase(AroundTheWorldId, true, AroundTheWorldId)]
    [TestCase("https://tracks.monochrome.st/track/155143832910958592", true, AroundTheWorldId)]
    [TestCase("https://tracks.monochrome.st/track/155143832910958592/", true, AroundTheWorldId)]
    [TestCase("https://mirror.example/track/12345", true, "12345")]
    [TestCase("https://example.com/song/155143832910958592", false, "")]
    [TestCase("https://example.com/song.mp3", false, "")]
    [TestCase("not-a-number", false, "")]
    [TestCase("", false, "")]
    [TestCase("123456789012345678901", false, "")] // 21 digits: no longer a snowflake
    public void TryExtractTrackId_HandlesBothShapes(string input, bool expected, string expectedId)
    {
        var ok = MonochromeDownloader.TryExtractTrackId(input, out var id);
        Assert.That(ok, Is.EqualTo(expected));
        Assert.That(id, Is.EqualTo(expectedId));
    }
}
