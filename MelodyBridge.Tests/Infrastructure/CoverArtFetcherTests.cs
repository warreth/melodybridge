using System.Net;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The cover art fetcher against a scripted HTTP boundary: what it accepts
/// (an image in size), what it refuses (other types, oversized bodies,
/// redirects to nowhere, file URLs) and that a repeated URL costs one
/// request. Nothing here may throw except caller cancellation.
/// </summary>
[TestFixture]
public class CoverArtFetcherTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Respond(request));
        }
    }

    private static CoverArtFetcher Create(StubHandler handler, TimeSpan? timeout = null)
    {
        var fetcher = new CoverArtFetcher(new HttpClient(handler), NullLogger<CoverArtFetcher>.Instance);
        if (timeout is { } t) fetcher.RequestTimeout = t;
        return fetcher;
    }

    private static HttpResponseMessage Image(byte[] bytes, string mediaType = "image/jpeg")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        SetContentType(response, mediaType);
        return response;
    }

    private static void SetContentType(HttpResponseMessage response, string mediaType)
        => response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

    [Test]
    public async Task Fetch_RealImage_ReturnsTheBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var handler = new StubHandler { Respond = _ => Image(bytes) };

        var result = await Create(handler).FetchAsync("https://cdn.example/cover.jpg");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(bytes));
            Assert.That(handler.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Fetch_SecondCallForTheSameUrl_IsCached()
    {
        var handler = new StubHandler { Respond = _ => Image(new byte[] { 9 }) };
        var fetcher = Create(handler);

        var first = await fetcher.FetchAsync("https://cdn.example/album.jpg");
        var second = await fetcher.FetchAsync("https://cdn.example/album.jpg");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.EqualTo(first), "an album shares one URL across its tracks");
            Assert.That(handler.Calls, Is.EqualTo(1), "the cover must be fetched once per album");
        });
    }

    [Test]
    public async Task Fetch_NotAnImage_IsNull()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>login</html>"),
                };
                SetContentType(response, "text/html");
                return response;
            },
        };

        Assert.That(await Create(handler).FetchAsync("https://cdn.example/cover.jpg"), Is.Null,
            "an error page served under 200 must never be embedded as art");
    }

    [Test]
    public async Task Fetch_ContentLengthOverTheCap_IsNull()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[16]),
                };
                SetContentType(response, "image/png");
                response.Content.Headers.ContentLength = 20 * 1024 * 1024;
                return response;
            },
        };

        Assert.That(await Create(handler).FetchAsync("https://cdn.example/huge.png"), Is.Null);
    }

    [Test]
    public async Task Fetch_BodyOverTheCapWithoutContentLength_IsNull()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                // A stream that keeps producing more than the cap; the
                // fetcher must stop reading, not buffer it all.
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EndlessStream(12 * 1024 * 1024)),
                };
                SetContentType(response, "image/jpeg");
                response.Content.Headers.ContentLength = null;
                return response;
            },
        };

        Assert.That(await Create(handler).FetchAsync("https://cdn.example/stream.jpg"), Is.Null);
    }

    /// <summary>Produces at most the requested number of bytes, then ends.</summary>
    private sealed class EndlessStream(long max) : Stream
    {
        private long _written;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => max;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_written >= max) return 0;
            var toWrite = (int)Math.Min(count, max - _written);
            Array.Clear(buffer, offset, toWrite);
            _written += toWrite;
            return toWrite;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public async Task Fetch_404_IsNull()
    {
        var handler = new StubHandler();
        Assert.That(await Create(handler).FetchAsync("https://cdn.example/gone.jpg"), Is.Null);
    }

    [Test]
    public async Task Fetch_Timeout_IsNull()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                Thread.Sleep(500);
                return Image(new byte[] { 1 });
            },
        };

        var result = await Create(handler, TimeSpan.FromMilliseconds(50))
            .FetchAsync("https://cdn.example/slow.jpg");

        Assert.That(result, Is.Null, "a slow host must not hold a download");
    }

    [Test]
    public async Task Fetch_CallerCancellation_Propagates()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                Thread.Sleep(200);
                return Image(new byte[] { 1 });
            },
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        Assert.CatchAsync<OperationCanceledException>(() =>
            Create(handler).FetchAsync("https://cdn.example/slow.jpg", cts.Token));
    }

    [TestCase("file:///etc/passwd")]
    [TestCase("not a url")]
    [TestCase("")]
    [TestCase(null)]
    public async Task Fetch_UnusableUrl_IsNullWithoutARequest(string? url)
    {
        var handler = new StubHandler();
        Assert.That(await Create(handler).FetchAsync(url), Is.Null);
        Assert.That(handler.Calls, Is.EqualTo(0), "only http(s) URLs are worth a request");
    }
}
