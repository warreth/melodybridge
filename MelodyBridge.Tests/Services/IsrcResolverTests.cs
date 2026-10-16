using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// ISRC enrichment against stubbed Deezer responses. Only a confident,
/// duration-plausible match may travel into the plugin searches: a wrong
/// ISRC would steer the ISRC-first lookups to the wrong recording.
/// </summary>
[TestFixture]
public class IsrcResolverTests
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

    private static IsrcResolver Create(StubHttpMessageHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com") };
        return new IsrcResolver(http, NullLogger<IsrcResolver>.Instance);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static object Hit(
        string title, string artist, string? isrc, int durationSeconds) => new
    {
        title,
        duration = durationSeconds,
        isrc,
        artist = new { name = artist },
    };

    [Test]
    public void ParseBestIsrc_ExactMatch_ReturnsTheCode()
    {
        var json = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "GBARL0600786", 211),
            },
        });

        Assert.That(IsrcResolver.ParseBestIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.EqualTo("GBARL0600786"));
    }

    [Test]
    public void ParseBestIsrc_WrongArtist_IsRejected()
    {
        var json = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                Hit("Never Gonna Give You Up", "Karaoke Channel", "XXAAA0000001", 211),
            },
        });

        Assert.That(IsrcResolver.ParseBestIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.Null, "a Low-confidence match must not supply an ISRC");
    }

    [Test]
    public void ParseBestIsrc_KaraokeVersion_IsRejected()
    {
        var json = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                Hit("Never Gonna Give You Up (Karaoke Version)", "Rick Astley", "XXAAA0000002", 211),
            },
        });

        Assert.That(IsrcResolver.ParseBestIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.Null,
            "an unrequested modifier is a different recording, never the track");
    }

    [Test]
    public void ParseBestIsrc_WrongLength_IsRejected()
    {
        // A 60s edit of a 3:33 track is a different recording; its ISRC
        // must not travel.
        var json = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "XXAAA0000003", 60),
            },
        });

        Assert.That(IsrcResolver.ParseBestIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.Null);
    }

    [Test]
    public void ParseBestIsrc_SkipsHitsWithoutAnIsrc()
    {
        var json = JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", null, 211),
                Hit("Never Gonna Give You Up", "Rick Astley", "GBARL0600786", 211),
            },
        });

        Assert.That(IsrcResolver.ParseBestIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.EqualTo("GBARL0600786"));
    }

    [Test]
    public void ParseBestIsrc_Garbage_ReturnsNull()
        => Assert.That(IsrcResolver.ParseBestIsrc("not json", "A", "T", null), Is.Null);

    [Test]
    public async Task ResolveAsync_QueriesDeezerOnce_AndCachesTheVerdict()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Json(new
        {
            data = new object[] { Hit("Flickermood", "Forss", "DEP960300042", 214) },
        });
        var resolver = Create(handler, out _);

        var first = await resolver.ResolveAsync("Forss", "Flickermood", TimeSpan.FromSeconds(213));
        var second = await resolver.ResolveAsync("Forss", "Flickermood", TimeSpan.FromSeconds(213));

        Assert.That(first, Is.EqualTo("DEP960300042"));
        Assert.That(second, Is.EqualTo("DEP960300042"));
        Assert.That(handler.RequestedUrls, Has.Count.EqualTo(1),
            "the second lookup is served from the cache");
        Assert.That(handler.RequestedUrls[0], Does.Contain("/search?q="));
    }

    [Test]
    public async Task ResolveAsync_FailingEndpoint_ReturnsNullWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        var resolver = Create(handler, out _);

        Assert.That(await resolver.ResolveAsync("A", "T", null), Is.Null);
    }

    // ── MusicBrainz fallback ─────────────────────────────────────────────

    private static Dictionary<string, object?> MbRecording(
        string title, string artist, string? isrc, int? lengthMs) => new()
    {
        ["title"] = title,
        ["length"] = lengthMs,
        ["isrcs"] = isrc is null ? Array.Empty<string>() : new[] { isrc },
        ["artist-credit"] = new object[]
        {
            new Dictionary<string, object?> { ["name"] = artist },
        },
    };

    private static IsrcResolver CreateWithFallback(
        StubHttpMessageHandler deezer, StubHttpMessageHandler musicBrainz)
        => new(
            new HttpClient(deezer) { BaseAddress = new Uri("https://api.deezer.com") },
            NullLogger<IsrcResolver>.Instance,
            new HttpClient(musicBrainz) { BaseAddress = new Uri("https://musicbrainz.org") });

    [Test]
    public void ParseBestMusicBrainzIsrc_ExactMatch_ReturnsTheCode()
    {
        var json = JsonSerializer.Serialize(new
        {
            recordings = new object[]
            {
                MbRecording("Never Gonna Give You Up", "Rick Astley", "GBARL0600786", 213560),
            },
        });

        Assert.That(IsrcResolver.ParseBestMusicBrainzIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.EqualTo("GBARL0600786"));
    }

    [Test]
    public void ParseBestMusicBrainzIsrc_WrongArtist_IsRejected()
    {
        var json = JsonSerializer.Serialize(new
        {
            recordings = new object[]
            {
                MbRecording("Never Gonna Give You Up", "Karaoke Channel", "XXAAA0000001", 213560),
            },
        });

        Assert.That(IsrcResolver.ParseBestMusicBrainzIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.Null, "a Low-confidence match must not supply an ISRC");
    }

    [Test]
    public void ParseBestMusicBrainzIsrc_SkipsRecordingsWithoutAnIsrc()
    {
        var json = JsonSerializer.Serialize(new
        {
            recordings = new object[]
            {
                MbRecording("Never Gonna Give You Up", "Rick Astley", null, 213560),
                MbRecording("Never Gonna Give You Up", "Rick Astley", "GBARL0600786", 213560),
            },
        });

        Assert.That(IsrcResolver.ParseBestMusicBrainzIsrc(json, "Rick Astley", "Never Gonna Give You Up",
            TimeSpan.FromSeconds(213)), Is.EqualTo("GBARL0600786"));
    }

    [Test]
    public void ParseBestMusicBrainzIsrc_Garbage_ReturnsNull()
        => Assert.That(IsrcResolver.ParseBestMusicBrainzIsrc("not json", "A", "T", null), Is.Null);

    [Test]
    public async Task ResolveAsync_DeezerMiss_FallsBackToMusicBrainz()
    {
        var deezer = new StubHttpMessageHandler
        {
            Respond = _ => Json(new { data = Array.Empty<object>() }),
        };
        var musicBrainz = new StubHttpMessageHandler
        {
            Respond = _ => Json(new
            {
                recordings = new object[]
                {
                    MbRecording("Never Gonna Give You Up", "Rick Astley", "GBARL0600786", 213560),
                },
            }),
        };
        var resolver = CreateWithFallback(deezer, musicBrainz);

        var isrc = await resolver.ResolveAsync("Rick Astley", "Never Gonna Give You Up", TimeSpan.FromSeconds(213));

        Assert.Multiple(() =>
        {
            Assert.That(isrc, Is.EqualTo("GBARL0600786"), "the fallback supplies the code");
            Assert.That(deezer.RequestedUrls, Has.Count.EqualTo(1));
            Assert.That(musicBrainz.RequestedUrls, Has.Count.EqualTo(1));
            Assert.That(musicBrainz.RequestedUrls[0], Does.Contain("/ws/2/recording?query="));
            Assert.That(musicBrainz.RequestedUrls[0], Does.Contain("fmt=json"));
        });
    }

    [Test]
    public async Task ResolveAsync_DeezerHit_DoesNotAskMusicBrainz()
    {
        var deezer = new StubHttpMessageHandler
        {
            Respond = _ => Json(new
            {
                data = new object[] { Hit("Flickermood", "Forss", "DEP960300042", 214) },
            }),
        };
        var musicBrainz = new StubHttpMessageHandler
        {
            Respond = _ => Json(new { recordings = Array.Empty<object>() }),
        };
        var resolver = CreateWithFallback(deezer, musicBrainz);

        var isrc = await resolver.ResolveAsync("Forss", "Flickermood", TimeSpan.FromSeconds(213));

        Assert.Multiple(() =>
        {
            Assert.That(isrc, Is.EqualTo("DEP960300042"));
            Assert.That(musicBrainz.RequestedUrls, Is.Empty, "the fallback only runs when Deezer misses");
        });
    }

    [Test]
    public async Task ResolveAsync_WithoutMusicBrainzClient_DeezerMiss_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => Json(new { data = Array.Empty<object>() }),
        };
        var resolver = Create(handler, out _);

        Assert.That(await resolver.ResolveAsync("A", "T", null), Is.Null);
    }
}
