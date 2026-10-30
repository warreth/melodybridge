using System.Net;
using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Qobuz catalogue search provider: ISRC-first requests, the shared
/// ranking and the token file the session reads. The download side lives
/// in <see cref="QobuzDownloaderTests"/>.
/// </summary>
[TestFixture]
public class QobuzSearchProviderTests
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
        var dir = Path.Combine(Path.GetTempPath(), $"mb-qobuz-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static QobuzClient.QobuzTrack Track(
        long id, string title, string artist, int duration, string? isrc = null, bool hires = false)
        => new(id, title, artist, TimeSpan.FromSeconds(duration), isrc,
            hires ? 24 : 16, hires, hires ? 96000 : 44100);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond(request));
    }

    private static HttpResponseMessage Text(string body, string type)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, type),
        };

    private static HttpResponseMessage BundleRespond(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.Contains("bundle.js"))
            return Text(
                """
                production:{api:{appId:"798273057",appSecret:"0123456789abcdef0123456789abcdef"}
                privateKey:"c8f4b3a2d1"
                a.initialSeed("ZjY5YTc3MzQ2ODZjYjk0Mjc2MjkzNz",window.utimezone.london)
                b.initialSeed("ODA2MzMxYzNiMGI2NDFkYTkyM2I4OT",window.utimezone.abidjan)
                c.initialSeed("YWJiMjEzNjQ5NDVjMDU4MzMwOTY2N2",window.utimezone.berlin)
                name:"app/Abidjan",info:"BhZWQwMWQwNGE=NTI2OGQwY2ZmZD",extras:"UwNGZkYzliMTRhY2YxYzI3MzExOWE="
                name:"app/London",info:"hhNGI3YWMzODE=MTlkMTI1OWM1Nj",extras:"VkNDNlNDg2OWU2MjU1YWUxYTdmZTU="
                name:"app/Berlin",info:"QxM2NhM2Q5M2E=ZWI4MDU5YWEyNT",extras:"RlNDZjNTkyNTAzYjdkZGMwOGI5MGQ="
                """, "application/javascript");
        return Text("<html><script src=\"/resources/8.2.0-b034/bundle.js\"></script></html>",
            "text/html");
    }

    private static QobuzSearchProvider Create(
        StubHttpMessageHandler handler, StubPluginConfig? config = null, string? homeRoot = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://play.qobuz.com") };
        var client = new QobuzClient(http, NullLogger<QobuzClient>.Instance);
        var session = new QobuzSession(config ?? new StubPluginConfig(),
            NullLogger<QobuzSession>.Instance, homeRoot);
        return new QobuzSearchProvider(client, session);
    }

    private static string QueryValue(Uri uri, string key)
        => uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2 && part[0] == key)
            .Select(part => Uri.UnescapeDataString(part[1]))
            .FirstOrDefault() ?? "";

    [Test]
    public void Rank_RealRiverPair_QobuzCreditsOnlyTheLead_AndStillWins()
    {
        // The user's case, pinned with the catalogue's own answer: Qobuz
        // carries both Revival ISRCs (USUM71713011 and USUM71712944) and
        // credits "Eminem" alone, while Spotify credits "Eminem, Ed
        // Sheeran" and advertises 226s against Qobuz's 221s. The exact
        // code must carry the hit.
        var hit = QobuzSearchProvider.Rank(new[]
        {
            new QobuzClient.QobuzTrack(46100068, "River (feat. Ed Sheeran)", "Eminem",
                TimeSpan.FromSeconds(221), "USUM71712944", null, false),
        }, new TrackQuery("Eminem, Ed Sheeran", "River", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(226), Isrc: "USUM71712944"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Does.EndWith("/46100068"));
    }

    [Test]
    public async Task Search_WithAnIsrc_AsksTheCatalogueForTheCodeFirst()
    {
        // The text search would return a soundtrack that shares the words;
        // the ISRC search must run first and win with the exact recording.
        var queries = new List<string>();
        var handler = new StubHttpMessageHandler
        {
            Respond = request =>
            {
                var url = request.RequestUri!.ToString();
                if (url.Contains("track/search"))
                {
                    var query = QueryValue(request.RequestUri, "query");
                    queries.Add(query);
                    return query == "USRC17607839"
                        ? Text("""{"tracks":{"items":[{"id":222,"title":"The Last Airbender","performer":{"name":"SLVL"},"duration":217,"isrc":"USRC17607839"}]}}""", "application/json")
                        : Text("""{"tracks":{"items":[{"id":999,"title":"The Last Airbender","performer":{"name":"Avatar Soundtrack"},"duration":300,"isrc":"OTHER00000001"}]}}""", "application/json");
                }
                // The page and the bundle the client needs for its app id.
                return BundleRespond(request);
            }
        };
        var config = new StubPluginConfig();
        config.Set("qobuz", QobuzSession.TokenKey, "token-xyz");
        var provider = Create(handler, config);

        var hit = await provider.SearchAsync("qobuz", new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.Multiple(() =>
        {
            Assert.That(queries, Is.Not.Empty);
            Assert.That(queries[0], Is.EqualTo("USRC17607839"),
                "the code is the first catalogue query");
            Assert.That(queries, Has.Count.EqualTo(1),
                "the exact code resolved it; the text search must not run");
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://play.qobuz.com/track/222"),
                "the ISRC recording wins over the soundtrack");
        });
    }

    [Test]
    public async Task Search_ForJumo_MapsTheQobuzIdsToAJumoUrl()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("track/search")
                ? Text("""
                    {"tracks":{"items":[{"id":9140024,"title":"One More Time",
                    "performer":{"name":"Daft Punk"},"duration":320,"isrc":"GBDUW0000053",
                    "album":{"id":"0886443927087","title":"Random Access Memories"}}]}}
                    """, "application/json")
                : BundleRespond(request),
        };
        var config = new StubPluginConfig();
        config.Set("qobuz", QobuzSession.TokenKey, "token-xyz");

        var hit = await Create(handler, config).SearchAsync("jumo",
            new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any,
                ExpectedDuration: TimeSpan.FromSeconds(320)));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("jumo:0886443927087:9140024"),
            "the Qobuz album id and track id are exactly what Jumo keys on");
    }

    [Test]
    public async Task Search_ForJumo_WithoutAnAlbumId_IsAMiss()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("track/search")
                ? Text("""
                    {"tracks":{"items":[{"id":9140024,"title":"One More Time",
                    "performer":{"name":"Daft Punk"},"duration":320}]}}
                    """, "application/json")
                : BundleRespond(request),
        };
        var config = new StubPluginConfig();
        config.Set("qobuz", QobuzSession.TokenKey, "token-xyz");

        var hit = await Create(handler, config).SearchAsync("jumo",
            new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any));

        Assert.That(hit, Is.Null,
            "without the album id the Jumo provider must stay the fallback");
    }

    [Test]
    public async Task Search_ForLucida_ReturnsTheQobuzUrl()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = request => request.RequestUri!.AbsolutePath.Contains("track/search")
                ? Text("""
                    {"tracks":{"items":[{"id":9140024,"title":"One More Time",
                    "performer":{"name":"Daft Punk"},"duration":320,
                    "album":{"id":"0886443927087"}}]}}
                    """, "application/json")
                : BundleRespond(request),
        };
        var config = new StubPluginConfig();
        config.Set("qobuz", QobuzSession.TokenKey, "token-xyz");

        var hit = await Create(handler, config).SearchAsync("lucida",
            new TrackQuery("Daft Punk", "One More Time", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://play.qobuz.com/track/9140024"),
            "Lucida consumes the official Qobuz URL directly");
    }

    [Test]
    public async Task Search_ForAnotherSource_IsAMissWithoutARequest()
    {
        var requests = 0;
        var handler = new StubHttpMessageHandler
        {
            Respond = request =>
            {
                requests++;
                return BundleRespond(request);
            },
        };
        var provider = Create(handler);

        var hit = await provider.SearchAsync("deemix",
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
        Assert.That(requests, Is.Zero, "the provider must only answer its own target");
    }

    // ── Ranking ──────────────────────────────────────────────────────────

    [Test]
    public void Rank_PicksTheRealMatch_NotTheFirstItem()
    {
        var hit = QobuzSearchProvider.Rank(new[]
        {
            Track(111, "Around the World (Remix)", "Some DJ", 240),
            Track(222, "Around the World", "Daft Punk", 429, "GBDUW0600009"),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://play.qobuz.com/track/222"));
            Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
        });
    }

    [Test]
    public void Rank_ExactIsrc_WinsOverACloserTitle()
    {
        var hit = QobuzSearchProvider.Rank(new[]
        {
            Track(111, "Around the World", "Daft Punk", 429, "OTHERISRC001"),
            Track(222, "Around the World (Remastered 2007)", "Daft Punk", 430, "GBDUW0600009"),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429), Isrc: "GBDUW0600009"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://play.qobuz.com/track/222"));
    }

    [Test]
    public void Rank_CarriesThePerTrackQualityFacts()
    {
        var hit = QobuzSearchProvider.Rank(new[]
        {
            Track(333, "Around the World", "Daft Punk", 429, hires: false),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(hit!.BitDepth, Is.EqualTo(16),
                "a CD-only track reports its own bit depth, not the configured tier");
            Assert.That(hit.SampleRateHz, Is.EqualTo(44100));
        });
    }

    [Test]
    public void Rank_NonStreamableHires_ReportsTheCdCeiling()
    {
        var hit = QobuzSearchProvider.Rank(new[]
        {
            new QobuzClient.QobuzTrack(444, "Around the World", "Daft Punk",
                TimeSpan.FromSeconds(429), null, 24, HiresStreamable: false, MaximumSamplingRate: 96000),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(hit!.BitDepth, Is.EqualTo(16),
                "a track whose hi-res is not streamable is a CD download");
            Assert.That(hit.SampleRateHz, Is.EqualTo(44100));
        });
    }

    [Test]
    public void Rank_Empty_IsNull()
        => Assert.That(QobuzSearchProvider.Rank(Array.Empty<QobuzClient.QobuzTrack>(),
            new TrackQuery("A", "T", DownloadQuality.Any)), Is.Null);

    [Test]
    public void ReadTokenFile_ReadsTheStoredToken_AndHandlesTheEdges()
    {
        var configFolder = Path.Combine(NewDir(), "qobuz-dl");
        Assert.That(QobuzSession.ReadTokenFile(configFolder), Is.Null, "no qobuz-dlp config yet");

        Directory.CreateDirectory(configFolder);
        File.WriteAllText(Path.Combine(configFolder, ".qobuz_dl.oauth.txt"), "  token-abc  \n");
        Assert.That(QobuzSession.ReadTokenFile(configFolder), Is.EqualTo("token-abc"));

        File.WriteAllText(Path.Combine(configFolder, ".qobuz_dl.oauth.txt"), "   ");
        Assert.That(QobuzSession.ReadTokenFile(configFolder), Is.Null, "an empty file is no token");
    }
}
