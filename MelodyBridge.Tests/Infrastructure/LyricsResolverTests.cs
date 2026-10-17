using System.Net;
using System.Text;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The lyrics resolver against a scripted LRCLIB: the query it builds, the
/// answers it accepts, the misses it remembers and the failures it does
/// not. It may never throw except caller cancellation, and it may never
/// slow a download down past its own budget.
/// </summary>
[TestFixture]
public class LyricsResolverTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Respond(request));
        }
    }

    private static LyricsResolver Create(StubHandler handler, TimeSpan? timeout = null)
    {
        var resolver = new LyricsResolver(new HttpClient(handler), NullLogger<LyricsResolver>.Instance);
        if (timeout is { } t) resolver.RequestTimeout = t;
        return resolver;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private const string HitBody = """
    {"id":1,"trackName":"Around the World","artistName":"Daft Punk",
     "albumName":"Homework","duration":430,"plainLyrics":"Around the world\nAround the world"}
    """;

    [Test]
    public async Task Resolve_SendsTheQueryAndReturnsPlainLyrics()
    {
        var handler = new StubHandler { Respond = _ => Json(HitBody) };

        var lyrics = await Create(handler).ResolveAsync(
            "Daft Punk", "Around the World", "Homework", TimeSpan.FromSeconds(429.7));

        Assert.Multiple(() =>
        {
            Assert.That(lyrics, Does.Contain("Around the world"));
            Assert.That(handler.Requests.Single(), Does.Contain("artist_name=Daft%20Punk"));
            Assert.That(handler.Requests.Single(), Does.Contain("track_name=Around%20the%20World"));
            Assert.That(handler.Requests.Single(), Does.Contain("album_name=Homework"));
            Assert.That(handler.Requests.Single(), Does.Contain("duration=430"),
                "LRCLIB matches on whole seconds");
            Assert.That(handler.Requests.Single(), Does.StartWith("https://lrclib.net/api/get"));
        });
    }

    [Test]
    public async Task Resolve_MissingTrack_IsNullAndRemembered()
    {
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var resolver = Create(handler);

        var first = await resolver.ResolveAsync("Nobody", "Nothing", duration: TimeSpan.FromSeconds(100));
        var second = await resolver.ResolveAsync("Nobody", "Nothing", duration: TimeSpan.FromSeconds(100));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Null);
            Assert.That(second, Is.Null);
            Assert.That(handler.Requests, Has.Count.EqualTo(1),
                "a definitive miss is remembered; the next run must not ask again");
        });
    }

    [Test]
    public async Task Resolve_ServerError_IsNullAndNotRemembered()
    {
        var handler = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
        };
        var resolver = Create(handler);

        await resolver.ResolveAsync("Artist", "Title");
        await resolver.ResolveAsync("Artist", "Title");

        Assert.That(handler.Requests, Has.Count.EqualTo(2),
            "a 5xx may recover, so it must not be cached as a miss");
    }

    [Test]
    public async Task Resolve_EmptyLyrics_IsNull()
    {
        var handler = new StubHandler
        {
            Respond = _ => Json("""{"trackName":"T","artistName":"A","plainLyrics":"   "}"""),
        };

        Assert.That(await Create(handler).ResolveAsync("A", "T"), Is.Null);
    }

    [Test]
    public async Task Resolve_Timeout_IsNull()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                Thread.Sleep(500);
                return Json(HitBody);
            },
        };

        var result = await Create(handler, TimeSpan.FromMilliseconds(50))
            .ResolveAsync("Daft Punk", "Around the World");

        Assert.That(result, Is.Null, "a slow lyrics host must not hold a download");
    }

    [Test]
    public async Task Resolve_CallerCancellation_Propagates()
    {
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                Thread.Sleep(200);
                return Json(HitBody);
            },
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        Assert.CatchAsync<OperationCanceledException>(() =>
            Create(handler).ResolveAsync("A", "T", ct: cts.Token));
    }

    [Test]
    public async Task Resolve_MissingArtistOrTitle_IsNullWithoutARequest()
    {
        var handler = new StubHandler();
        var resolver = Create(handler);

        var noArtist = await resolver.ResolveAsync(null, "Title");
        var noTitle = await resolver.ResolveAsync("Artist", "  ");

        Assert.Multiple(() =>
        {
            Assert.That(noArtist, Is.Null);
            Assert.That(noTitle, Is.Null);
            Assert.That(handler.Requests, Is.Empty);
        });
    }
}
