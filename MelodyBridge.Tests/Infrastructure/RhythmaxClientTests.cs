using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MelodyBridge.Infrastructure.Rhythmax;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Rhythmax manifest and DASH parsing against the exact shapes the public
/// hifi API returns, including the custom fork's flat envelope and the
/// nested Tidal OpenAPI one. No live network.
/// </summary>
[TestFixture]
public class RhythmaxClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public List<string?> Authorizations { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            Authorizations.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RateLimited()
    {
        var response = Json("{}", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static RhythmaxClient Client(ScriptedHandler handler)
        => new(new HttpClient(handler), "https://hifi.test",
            NullLogger<RhythmaxClient>.Instance);

    private const string FlatManifest = """
    {
      "data": {
        "id": "35986110",
        "type": "trackManifests",
        "attributes": {
          "trackPresentation": "FULL",
          "uri": "https://manifest.test/track.mpd?token=abc",
          "formats": ["FLAC"],
          "hash": "x"
        }
      }
    }
    """;

    private const string NestedManifest = """
    {
      "data": {
        "data": {
          "id": "35986110",
          "type": "trackManifests",
          "attributes": {
            "trackPresentation": "FULL",
            "uri": "https://manifest.test/track.mpd?token=abc",
            "formats": ["FLAC", "AACLC"]
          }
        },
        "links": { "self": "/trackManifests/35986110" }
      }
    }
    """;

    private const string Dash = """
    <?xml version="1.0" encoding="UTF-8"?>
    <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4M16S">
      <Period id="0">
        <AdaptationSet id="0" contentType="audio" mimeType="audio/mp4" segmentAlignment="true">
          <Representation id="FLAC,44100,16" codecs="flac" bandwidth="909745" audioSamplingRate="44100">
            <SegmentTemplate timescale="44100"
              initialization="https://cdn.test/init.mp4?token=a&amp;id=b"
              media="https://cdn.test/$Number$.mp4?token=a&amp;id=b"
              startNumber="1">
              <SegmentTimeline>
                <S d="176128" r="2"/>
                <S d="86016"/>
              </SegmentTimeline>
            </SegmentTemplate>
            <Label>FLAC</Label>
          </Representation>
          <Representation id="AAC,44100" codecs="mp4a.40.2" bandwidth="320000" audioSamplingRate="44100">
            <SegmentTemplate timescale="44100"
              initialization="https://cdn.test/aac-init.mp4"
              media="https://cdn.test/aac-$Number$.mp4"
              startNumber="1">
              <SegmentTimeline><S d="176128" r="3"/></SegmentTimeline>
            </SegmentTemplate>
          </Representation>
        </AdaptationSet>
      </Period>
    </MPD>
    """;

    // ── Manifest envelope ────────────────────────────────────────────────

    [Test]
    public void ParseManifest_ReadsTheCustomForkShape()
    {
        var result = RhythmaxClient.ParseManifest(FlatManifest);

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Ok));
        Assert.That(result.ManifestUrl, Is.EqualTo("https://manifest.test/track.mpd?token=abc"));
        Assert.That(result.Formats, Is.EquivalentTo(new[] { "FLAC" }));
    }

    [Test]
    public void ParseManifest_ReadsTheNestedOpenApiShape()
    {
        var result = RhythmaxClient.ParseManifest(NestedManifest);

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Ok));
        Assert.That(result.ManifestUrl, Is.EqualTo("https://manifest.test/track.mpd?token=abc"));
        Assert.That(result.Formats, Is.EquivalentTo(new[] { "FLAC", "AACLC" }));
    }

    [Test]
    public void ParseManifest_PreviewIsNotFull()
    {
        var result = RhythmaxClient.ParseManifest("""
        {
          "data": { "attributes": {
            "trackPresentation": "PREVIEW",
            "uri": "https://manifest.test/preview.mpd"
          } }
        }
        """);

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Preview));
        Assert.That(result.Message, Does.Contain("preview"));
    }

    [Test]
    public void ParseManifest_MissingUriIsAReadableFailure()
    {
        var result = RhythmaxClient.ParseManifest("""{"data":{"attributes":{"trackPresentation":"FULL"}}}""");

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Unreachable));
        Assert.That(result.Message, Does.Contain("without a URL"));
    }

    // ── DASH ─────────────────────────────────────────────────────────────

    [Test]
    public void ParseDash_BuildsInitAndEveryNumberedSegment()
    {
        var plan = RhythmaxClient.ParseDash(Dash, "https://manifest.test/track.mpd", "flac");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.Codecs, Is.EqualTo("flac"));
        Assert.That(plan.InitializationUrl,
            Is.EqualTo("https://cdn.test/init.mp4?token=a&id=b"));
        Assert.That(plan.SegmentUrls, Has.Count.EqualTo(4));
        Assert.That(plan.SegmentUrls[0], Is.EqualTo("https://cdn.test/1.mp4?token=a&id=b"));
        Assert.That(plan.SegmentUrls[3], Is.EqualTo("https://cdn.test/4.mp4?token=a&id=b"));
    }

    [Test]
    public void ParseDash_PicksTheAacRepresentationForAnAacRequest()
    {
        var plan = RhythmaxClient.ParseDash(Dash, "https://manifest.test/track.mpd", "aac");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.Codecs, Is.EqualTo("mp4a.40.2"));
        Assert.That(plan.SegmentUrls, Has.Count.EqualTo(4));
        Assert.That(plan.SegmentUrls[0], Is.EqualTo("https://cdn.test/aac-1.mp4"));
    }

    [Test]
    public void ParseDash_SupportsSegmentList()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period id="0"><AdaptationSet mimeType="audio/mp4">
            <Representation id="0" codecs="flac">
              <SegmentList timescale="44100">
                <Initialization sourceURL="https://cdn.test/list-init.mp4"/>
                <SegmentURL media="https://cdn.test/list-1.mp4"/>
                <SegmentURL media="https://cdn.test/list-2.mp4"/>
              </SegmentList>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        var plan = RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "flac");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.InitializationUrl, Is.EqualTo("https://cdn.test/list-init.mp4"));
        Assert.That(plan.SegmentUrls,
            Is.EqualTo(new[] { "https://cdn.test/list-1.mp4", "https://cdn.test/list-2.mp4" }));
    }

    [Test]
    public void ParseDash_MultiPeriodSkipsVideoAndPicksTheAudioPeriod()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period id="video"><AdaptationSet mimeType="video/mp4">
            <Representation id="v" codecs="avc1.42E01E">
              <SegmentList><Initialization sourceURL="https://cdn.test/v-init.mp4"/>
                <SegmentURL media="https://cdn.test/v-1.mp4"/></SegmentList>
            </Representation>
          </AdaptationSet></Period>
          <Period id="audio"><AdaptationSet mimeType="audio/mp4">
            <Representation id="a" codecs="mp4a.40.2">
              <SegmentList><Initialization sourceURL="https://cdn.test/a-init.mp4"/>
                <SegmentURL media="https://cdn.test/a-1.mp4"/></SegmentList>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        var plan = RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "aac");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.Codecs, Is.EqualTo("mp4a.40.2"));
        Assert.That(plan.InitializationUrl, Is.EqualTo("https://cdn.test/a-init.mp4"));
        Assert.That(plan.SegmentUrls, Is.EqualTo(new[] { "https://cdn.test/a-1.mp4" }));
    }

    [Test]
    public void ParseDash_SubstitutesTimeInSegmentTemplate()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period id="0"><AdaptationSet mimeType="audio/mp4">
            <Representation id="0" codecs="flac">
              <SegmentTemplate timescale="44100"
                initialization="https://cdn.test/init.mp4"
                media="https://cdn.test/$Time$.mp4" startNumber="1">
                <SegmentTimeline>
                  <S t="0" d="100" r="1"/>
                  <S d="50"/>
                </SegmentTimeline>
              </SegmentTemplate>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        var plan = RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "flac");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.SegmentUrls,
            Is.EqualTo(new[]
            {
                "https://cdn.test/0.mp4",
                "https://cdn.test/100.mp4",
                "https://cdn.test/200.mp4",
            }));
    }

    [Test]
    public void ParseDash_SegmentTemplateWithDuration_UsesThePresentationDuration()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static"
             mediaPresentationDuration="PT30S">
          <Period id="0"><AdaptationSet mimeType="audio/mp4">
            <Representation id="0" codecs="flac">
              <SegmentTemplate timescale="44100" duration="441000"
                initialization="https://cdn.test/init-$Number$.mp4"
                media="https://cdn.test/seg-$Number$.mp4" startNumber="1"/>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        var plan = RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "flac");

        Assert.That(plan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(plan!.InitializationUrl, Is.EqualTo("https://cdn.test/init-1.mp4"));
            Assert.That(plan.SegmentUrls, Is.EqualTo(new[]
            {
                "https://cdn.test/seg-1.mp4",
                "https://cdn.test/seg-2.mp4",
                "https://cdn.test/seg-3.mp4",
            }), "30 seconds at ten seconds per segment");
        });
    }

    [Test]
    public void ParseDash_SegmentBase_UsesTheInitAndIndexRanges()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period id="0"><AdaptationSet mimeType="audio/mp4">
            <Representation id="0" codecs="flac" indexRange="1000-1999">
              <BaseURL>https://cdn.test/audio.mp4</BaseURL>
              <SegmentBase>
                <Initialization range="0-999"/>
              </SegmentBase>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        var plan = RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "flac");

        Assert.That(plan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(plan!.InitializationUrl, Is.EqualTo("https://cdn.test/audio.mp4"));
            Assert.That(plan.InitializationRange,
                Is.EqualTo(new RhythmaxClient.ByteRange(0, 1000)));
            Assert.That(plan.SegmentUrls, Is.EqualTo(new[] { "https://cdn.test/audio.mp4" }));
            Assert.That(plan.MediaRange,
                Is.EqualTo(new RhythmaxClient.ByteRange(2000)),
                "the media starts after both the init and the index");
        });
    }

    [Test]
    public void ParseDash_SegmentBaseWithoutInit_StartsAfterTheIndex()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period id="0"><AdaptationSet mimeType="audio/mp4">
            <Representation id="0" codecs="mp4a.40.2" indexRange="0-99">
              <BaseURL>https://cdn.test/audio.mp4</BaseURL>
              <SegmentBase/>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        var plan = RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "aac");

        Assert.That(plan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(plan!.InitializationUrl, Is.Empty, "there is no separate init range");
            Assert.That(plan.InitializationRange, Is.Null);
            Assert.That(plan.MediaRange, Is.EqualTo(new RhythmaxClient.ByteRange(100)));
        });
    }

    [Test]
    public void ParseDash_SegmentTemplateDurationWithoutTotal_ReturnsNull()
    {
        const string xml = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period id="0"><AdaptationSet mimeType="audio/mp4">
            <Representation id="0" codecs="flac">
              <SegmentTemplate timescale="44100" duration="441000"
                initialization="https://cdn.test/init.mp4"
                media="https://cdn.test/seg-$Number$.mp4"/>
            </Representation>
          </AdaptationSet></Period>
        </MPD>
        """;

        Assert.That(RhythmaxClient.ParseDash(xml, "https://manifest.test/track.mpd", "flac"),
            Is.Null, "without a duration there is no honest segment count");
    }

    [Test]
    public void ParseDash_RejectsUnreadableOrEmptyManifests()
    {
        Assert.That(RhythmaxClient.ParseDash("<not-a-manifest>", "https://x/y.mpd", "flac"), Is.Null);
        Assert.That(RhythmaxClient.ParseDash("<MPD/>", "https://x/y.mpd", "flac"), Is.Null);
    }

    // ── HTTP flow ────────────────────────────────────────────────────────

    [Test]
    public async Task GetManifest_FetchesTokenThenManifest()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = request => request.RequestUri!.AbsoluteUri switch
        {
            "https://hifi.test/token" => Json("""{"access_token":"play-token","expires_in":600}"""),
            var url when url.Contains("/manifests") => Json(FlatManifest),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = Client(handler);

        var result = await client.GetManifestAsync(35986110, "LOSSLESS");

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Ok));
        Assert.That(handler.Requests[0], Is.EqualTo("https://hifi.test/token"));
        Assert.That(handler.Requests[1], Does.Contain("/manifests?id=35986110&quality=LOSSLESS"));
    }

    [Test]
    public async Task GetManifest_RefreshesOnceAfterAnUnauthorizedAnswer()
    {
        using var handler = new ScriptedHandler();
        var tokenCalls = 0;
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://hifi.test/token")
            {
                tokenCalls++;
                return Json(tokenCalls == 1
                    ? """{"access_token":"stale-play-token","expires_in":600}"""
                    : """{"access_token":"fresh-play-token","expires_in":600}""");
            }
            return request.Headers.Authorization?.Parameter == "stale-play-token"
                ? Json("{}", HttpStatusCode.Unauthorized)
                : Json(FlatManifest);
        };
        var client = Client(handler);

        var result = await client.GetManifestAsync(35986110, "LOSSLESS");

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Ok));
        Assert.That(tokenCalls, Is.EqualTo(2));
        var manifestAuth = handler.Requests
            .Select((url, index) => (url, auth: handler.Authorizations[index]))
            .Where(r => r.url.Contains("/manifests"))
            .Select(r => r.auth)
            .ToList();
        Assert.That(manifestAuth,
            Is.EqualTo(new[] { "stale-play-token", "fresh-play-token" }),
            "the retried manifest must use the refreshed token");
    }

    [Test]
    public async Task GetManifest_LongRetryAfter_ReturnsRateLimitedWithoutWaiting()
    {
        using var handler = new ScriptedHandler();
        var manifestCalls = 0;
        handler.Respond = request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://hifi.test/token")
                return Json("""{"access_token":"play-token","expires_in":600}""");
            manifestCalls++;
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await Client(handler).GetManifestAsync(35986110, "LOSSLESS");
        stopwatch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.RateLimited));
            Assert.That(manifestCalls, Is.EqualTo(1),
                "a Retry-After above the 10 s budget is not retried");
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
                "the waterfall must not sit on a full pool");
        });
    }

    [Test]
    public async Task ManifestCallerCancellation_Propagates()
    {
        using var handler = new CancellingHandler();
        var client = new RhythmaxClient(
            new HttpClient(handler), "https://hifi.test",
            NullLogger<RhythmaxClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.CatchAsync<OperationCanceledException>(() =>
            client.GetManifestAsync(35986110, "LOSSLESS", cts.Token));
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task GetManifest_PollsAQueuedPlaybackRequest()
    {
        using var handler = new ScriptedHandler();
        var manifestCalls = 0;
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://hifi.test/token")
                return Json("""{"access_token":"play-token","expires_in":600}""");
            if (url.Contains("/manifests"))
            {
                manifestCalls++;
                return Json("""
                  {"status":"pending","statusUrl":"https://hifi.test/queue/1","retryAfter":1}
                  """, HttpStatusCode.Accepted);
            }
            return url == "https://hifi.test/queue/1"
                ? Json(FlatManifest)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var result = await Client(handler).GetManifestAsync(35986110, "LOSSLESS");

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Ok));
        Assert.That(manifestCalls, Is.EqualTo(1));
        Assert.That(handler.Requests, Does.Contain("https://hifi.test/queue/1"));
    }

    [Test]
    public async Task GetManifest_RetriesARateLimitAndThenSucceeds()
    {
        using var handler = new ScriptedHandler();
        var manifestCalls = 0;
        handler.Respond = request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://hifi.test/token")
                return Json("""{"access_token":"play-token","expires_in":600}""");
            manifestCalls++;
            return manifestCalls < 3 ? RateLimited() : Json(FlatManifest);
        };

        var result = await Client(handler).GetManifestAsync(35986110, "LOSSLESS");

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.Ok));
        Assert.That(manifestCalls, Is.EqualTo(3), "two retries after the first 429");
    }

    [Test]
    public async Task GetManifest_RateLimitIsItsOwnStatusAfterRetries()
    {
        using var handler = new ScriptedHandler();
        var manifestCalls = 0;
        handler.Respond = request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://hifi.test/token")
                return Json("""{"access_token":"play-token","expires_in":600}""");
            manifestCalls++;
            return RateLimited();
        };

        var result = await Client(handler).GetManifestAsync(35986110, "LOSSLESS");

        Assert.That(result.Status, Is.EqualTo(RhythmaxClient.ManifestStatus.RateLimited));
        Assert.That(result.Message, Does.Contain("rate limited"));
        Assert.That(manifestCalls, Is.EqualTo(3));
    }

    [Test]
    public async Task Health_ReportsPreviewAsNotReady()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = _ => Json("""{"status":"PREVIEW","active":1}""");
        var client = Client(handler);

        var (ready, reason) = await client.CheckHealthAsync();

        Assert.That(ready, Is.False);
        Assert.That(reason, Does.Contain("preview"));
    }

    [Test]
    public async Task Health_ReportsFullAsReady()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = _ => Json("""{"status":"FULL","active":1}""");
        var client = Client(handler);

        var (ready, reason) = await client.CheckHealthAsync();

        Assert.That(ready, Is.True);
        Assert.That(reason, Is.Null);
    }

    [Test]
    public async Task Health_ReportsDeadAndNoActiveAccount()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = _ => Json("""{"status":"DEAD","active":0}""");
        var client = Client(handler);

        var (ready, reason) = await client.CheckHealthAsync();

        Assert.That(ready, Is.False);
        Assert.That(reason, Does.Contain("no working Tidal account"));
    }

    [Test]
    public async Task Health_ReportsAnHttpError()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = _ => Json("{}", HttpStatusCode.InternalServerError);
        var client = Client(handler);

        var (ready, reason) = await client.CheckHealthAsync();

        Assert.That(ready, Is.False);
        Assert.That(reason, Does.Contain("500"));
    }

    [Test]
    public async Task Health_MalformedJsonIsUnavailable()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain"),
        };
        var client = Client(handler);

        var (ready, reason) = await client.CheckHealthAsync();

        Assert.That(ready, Is.False);
        Assert.That(reason, Is.Not.Null.And.Not.Empty);
    }
}
