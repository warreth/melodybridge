using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Qobuz;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Qobuz search client: the bundle scrape (with fragments captured
/// from the live bundle, expected secrets computed independently), the
/// token check and the search parsing. No live network access needed.
/// </summary>
[TestFixture]
public class QobuzClientTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static QobuzClient Create(StubHttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://play.qobuz.com") },
            NullLogger<QobuzClient>.Instance);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage EmptySearch() => Json(new
    {
        tracks = new { items = Array.Empty<object>() },
    });

    /// <summary>Fragments captured from the live bundle; secrets computed independently.</summary>
    private const string BundleFixture = """
        a.initialSeed("ZjY5YTc3MzQ2ODZjYjk0Mjc2MjkzNz",window.utimezone.london)
        b.initialSeed("ODA2MzMxYzNiMGI2NDFkYTkyM2I4OT",window.utimezone.abidjan)
        c.initialSeed("YWJiMjEzNjQ5NDVjMDU4MzMwOTY2N2",window.utimezone.berlin)
        name:"app/Abidjan",info:"BhZWQwMWQwNGE=NTI2OGQwY2ZmZD",extras:"UwNGZkYzliMTRhY2YxYzI3MzExOWE="
        name:"app/London",info:"hhNGI3YWMzODE=MTlkMTI1OWM1Nj",extras:"VkNDNlNDg2OWU2MjU1YWUxYTdmZTU="
        name:"app/Berlin",info:"QxM2NhM2Q5M2E=ZWI4MDU5YWEyNT",extras:"RlNDZjNTkyNTAzYjdkZGMwOGI5MGQ="
        """;

    [Test]
    public void ParseSecrets_DerivesTheLiveBundleSecrets()
    {
        var secrets = QobuzClient.ParseSecrets(BundleFixture);

        Assert.That(secrets, Is.EqualTo(new[]
        {
            "806331c3b0b641da923b890aed01d04a", // abidjan, moved to the front
            "f69a7734686cb9427629378a4b7ac381", // london
            "abb21364945c0583309667d13ca3d93a", // berlin
        }), "the secret derivation must match the live bundle");
    }

    [Test]
    public void ParseSecrets_Garbage_IsEmpty() =>
        Assert.That(QobuzClient.ParseSecrets("no seeds here"), Is.Empty);

    private static HttpResponseMessage BundleResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                <html><script src="/resources/8.2.0-b034/bundle.js"></script></html>
                """, Encoding.UTF8, "text/html"),
        };

    private static HttpResponseMessage BundleScriptResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                production:{api:{appId:"798273057",appSecret:"0123456789abcdef0123456789abcdef"}
                privateKey:"c8f4b3a2d1"
                """ + BundleFixture, Encoding.UTF8, "application/javascript"),
        };

    [Test]
    public async Task GetBundle_ScrapesAppIdPrivateKeyAndSecrets()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("bundle.js")
            ? BundleScriptResponse()
            : BundleResponse();
        var client = Create(handler);

        var bundle = await client.GetBundleAsync();

        Assert.That(bundle, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(bundle!.AppId, Is.EqualTo("798273057"));
            Assert.That(bundle.PrivateKey, Is.EqualTo("c8f4b3a2d1"));
            Assert.That(bundle.Secrets, Has.Count.EqualTo(3));
            Assert.That(handler.Requests, Has.Count.EqualTo(2), "login page and bundle script");
        });
    }

    [Test]
    public async Task GetBundle_LoginPageWithoutBundleScript_IsNull()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>no script here</html>", Encoding.UTF8, "text/html"),
        };
        var client = Create(handler);

        Assert.That(await client.GetBundleAsync(), Is.Null);
    }

    [Test]
    public async Task ValidateToken_SendsTheHeaders_AndReadsThePlan()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("track/search")) return EmptySearch();
            if (path.Contains("login") && request.Method == HttpMethod.Post)
                return Json(new
                {
                    user = new { credential = new { parameters = new { short_label = "Studio" } } },
                });
            return BundleResponse();
        };
        var client = Create(handler);

        var check = await client.ValidateTokenAsync("token-123");

        Assert.That(check.Account, Is.Not.Null);
        var account = check.Account!;
        Assert.Multiple(() =>
        {
            Assert.That(check.Status, Is.EqualTo(QobuzClient.QobuzTokenStatus.Valid));
            Assert.That(account.Label, Is.EqualTo("Studio"));
            Assert.That(account.CanDownload, Is.True);

            var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.That(post.Headers.GetValues("X-App-Id").Single(), Is.EqualTo("798273057"));
            Assert.That(post.Headers.GetValues("X-User-Auth-Token").Single(), Is.EqualTo("token-123"));
        });
    }

    [Test]
    public async Task ValidateToken_FreePlan_CannotDownload()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("track/search")) return EmptySearch();
            if (request.Method == HttpMethod.Post)
                return Json(new { user = new { credential = new { parameters = (object?)null } } });
            return BundleResponse();
        };
        var client = Create(handler);

        var check = await client.ValidateTokenAsync("token-123");

        Assert.That(check.Account, Is.Not.Null);
        Assert.That(check.Account!.CanDownload, Is.False, "a free plan has no parameters block");
    }

    [Test]
    public async Task ValidateToken_RefusedToken_IsRejected()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("bundle.js")
            ? BundleScriptResponse()
            : request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : BundleResponse();
        var client = Create(handler);

        var check = await client.ValidateTokenAsync("stale-token");

        Assert.Multiple(() =>
        {
            Assert.That(check.Status, Is.EqualTo(QobuzClient.QobuzTokenStatus.Rejected),
                "401 is the token being refused, not a network problem");
            Assert.That(check.Account, Is.Null);
        });
    }

    [Test]
    public async Task ValidateToken_BlockedAccount_IsBlocked_NotRejected()
    {
        // The user's live case: user/login says 200 and the token is
        // real, but every catalogue call answers USER_BLOCKED.
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("track/search"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"status":"error","code":403,"message":"Account is blocked","error_code":"USER_BLOCKED"}""",
                        Encoding.UTF8, "application/json"),
                };
            if (path.Contains("login") && request.Method == HttpMethod.Post)
                return Json(new
                {
                    user = new { credential = new { parameters = new { short_label = "Studio" } } },
                });
            return BundleResponse();
        };
        var client = Create(handler);

        var check = await client.ValidateTokenAsync("token-123");

        Assert.Multiple(() =>
        {
            Assert.That(check.Status, Is.EqualTo(QobuzClient.QobuzTokenStatus.Blocked),
                "a blocked account is not a stale token");
            Assert.That(check.Message, Does.Contain("USER_BLOCKED"));
        });
    }

    [Test]
    public async Task Search_BlockedAccount_ReportsBlocked()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("track/search"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"status":"error","code":403,"message":"Account is blocked","error_code":"USER_BLOCKED"}""",
                        Encoding.UTF8, "application/json"),
                };
            return BundleResponse();
        };
        var client = Create(handler);

        var result = await client.SearchTracksAsync("Daft Punk", 5, "token-123");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(QobuzClient.QobuzSearchStatus.Blocked));
            Assert.That(result.Tracks, Is.Empty);
            Assert.That(result.Message, Does.Contain("blocked"));
        });
    }

    [Test]
    public async Task Search_GzipBlockedBody_IsDecodedBeforeClassifying()
    {
        // The live API answers 403 with a gzipped JSON body. Reading the
        // compressed bytes as UTF-8 lost USER_BLOCKED and called a blocked
        // account a rejected token.
        var payload = Encoding.UTF8.GetBytes(
            """{"status":"error","code":403,"message":"Account is blocked","error_code":"USER_BLOCKED"}""");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(payload);

        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("track/search"))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new ByteArrayContent(compressed.ToArray()),
                };
                response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            }
            return BundleResponse();
        };
        var client = Create(handler);

        var result = await client.SearchTracksAsync("Daft Punk", 5, "token-123");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(QobuzClient.QobuzSearchStatus.Blocked));
            Assert.That(result.Message, Does.Contain("USER_BLOCKED"));
        });
    }

    [Test]
    public async Task Search_GzipSuccessBody_IsDecodedAndParsed()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {"tracks":{"items":[{"id":5966783,"title":"Around the World","duration":429,
            "isrc":"GBDUW0600009","maximum_bit_depth":24,"maximum_sampling_rate":96,
            "hires_streamable":true,"performer":{"name":"Daft Punk"}}]}}
            """);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(payload);

        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("track/search"))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(compressed.ToArray()),
                };
                response.Content.Headers.ContentEncoding.Add("gzip");
                return response;
            }
            return BundleResponse();
        };
        var client = Create(handler);

        var result = await client.SearchTracksAsync("Daft Punk", 5, "token-123");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(QobuzClient.QobuzSearchStatus.Ok));
            Assert.That(result.Tracks, Has.Count.EqualTo(1));
            Assert.That(result.Tracks[0].Title, Is.EqualTo("Around the World"));
            Assert.That(result.Tracks[0].MaximumSamplingRate, Is.EqualTo(96000));
        });
    }

    [Test]
    public async Task ValidateToken_ServerError_IsUnreachable_NotRejected()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request => request.RequestUri!.AbsolutePath.Contains("bundle.js")
            ? BundleScriptResponse()
            : request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : BundleResponse();
        var client = Create(handler);

        var check = await client.ValidateTokenAsync("token-123");

        Assert.That(check.Status, Is.EqualTo(QobuzClient.QobuzTokenStatus.Unreachable),
            "a 500 says nothing about the token, so the UI must not call it stale");
    }

    [Test]
    public async Task Search_ParsesTheTrackShape_AndSendsTheToken()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleScriptResponse();
            if (path.Contains("login")) return BundleResponse();
            if (path.Contains("track/search"))
                return Json(new
                {
                    tracks = new
                    {
                        items = new object[]
                        {
                            new
                            {
                                id = 5966783,
                                title = "Around the World",
                                duration = 429,
                                isrc = "GBDUW0600009",
                                maximum_bit_depth = 24,
                                maximum_sampling_rate = 96,
                                hires_streamable = true,
                                performer = new { name = "Daft Punk" },
                            },
                            new
                            {
                                id = 5966784,
                                title = "A CD Track",
                                duration = 200,
                                isrc = "GBDUW0600010",
                                maximum_bit_depth = 16,
                                maximum_sampling_rate = 44.1,
                                hires_streamable = false,
                                performer = new { name = "Daft Punk" },
                            },
                        },
                    },
                });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var client = Create(handler);

        var result = await client.SearchTracksAsync("Daft Punk Around the World", 5, "token-123");
        var tracks = result.Tracks;

        Assert.That(tracks, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(QobuzClient.QobuzSearchStatus.Ok));
            Assert.That(tracks[0].Id, Is.EqualTo(5966783));
            Assert.That(tracks[0].Title, Is.EqualTo("Around the World"));
            Assert.That(tracks[0].Artist, Is.EqualTo("Daft Punk"));
            Assert.That(tracks[0].Duration, Is.EqualTo(TimeSpan.FromSeconds(429)));
            Assert.That(tracks[0].Isrc, Is.EqualTo("GBDUW0600009"));
            Assert.That(tracks[0].MaximumBitDepth, Is.EqualTo(24));
            Assert.That(tracks[0].MaximumSamplingRate, Is.EqualTo(96000),
                "96 kHz arrives as 96 in the live payload");
            Assert.That(tracks[0].HiresStreamable, Is.True);
            Assert.That(tracks[1].MaximumSamplingRate, Is.EqualTo(44100),
                "44.1 kHz must not make the parser throw");
            Assert.That(tracks[1].HiresStreamable, Is.False);

            var search = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.Contains("track/search"));
            Assert.That(search.Headers.GetValues("X-User-Auth-Token").Single(), Is.EqualTo("token-123"));
        });
    }
}
