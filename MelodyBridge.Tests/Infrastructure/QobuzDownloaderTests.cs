using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The qobuz-dlp plugin: the native search (bundle + token) for the
/// ranking, and the user's own qobuz-dlp CLI for the download. The CLI
/// is a POSIX shell stub that writes a real (sniffable) audio file, so
/// the whole path runs without Python or a Qobuz account.
/// </summary>
[TestFixture]
public class QobuzDownloaderTests
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
        var dir = Path.Combine(Path.GetTempPath(), $"mb-qobuz-{Guid.NewGuid():N}");
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
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond(request));
    }

    private static HttpResponseMessage Text(string body, string type)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, type),
        };

    /// <summary>The synthetic web player bundle the config seeding reads.</summary>
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

    private static QobuzDownloader Create(StubPluginConfig? config = null, string? homeRoot = null)
    {
        var http = new HttpClient(new StubHttpMessageHandler { Respond = BundleRespond })
        {
            BaseAddress = new Uri("https://play.qobuz.com"),
        };
        return new QobuzDownloader(
            new QobuzClient(http, NullLogger<QobuzClient>.Instance),
            config ?? new StubPluginConfig(),
            NullLogger<QobuzDownloader>.Instance,
            homeRoot);
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
        var hit = QobuzDownloader.Rank(new[]
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
        var http = new HttpClient(new StubHttpMessageHandler
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
        })
        { BaseAddress = new Uri("https://play.qobuz.com") };
        var config = new StubPluginConfig();
        config.Set("qobuz", "user_auth_token", "token-xyz");
        var plugin = new QobuzDownloader(
            new QobuzClient(http, NullLogger<QobuzClient>.Instance), config,
            NullLogger<QobuzDownloader>.Instance);

        var hit = await plugin.SearchAsync(new TrackQuery(
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

    // ── Ranking ──────────────────────────────────────────────────────────

    [Test]
    public void Rank_PicksTheRealMatch_NotTheFirstItem()
    {
        var hit = QobuzDownloader.Rank(new[]
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
        var hit = QobuzDownloader.Rank(new[]
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
        var hit = QobuzDownloader.Rank(new[]
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
        var hit = QobuzDownloader.Rank(new[]
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
        => Assert.That(QobuzDownloader.Rank(Array.Empty<QobuzClient.QobuzTrack>(),
            new TrackQuery("A", "T", DownloadQuality.Any)), Is.Null);

    // ── Mapping helpers ──────────────────────────────────────────────────

    [TestCase(AudioFormat.Flac, "6", "6")]
    [TestCase(AudioFormat.Flac, "27", "27")]
    [TestCase(AudioFormat.Mp3, "6", "5")]
    [TestCase(AudioFormat.Aac, "6", null)]
    [TestCase(AudioFormat.Auto, "7", "7")]
    public void QualityFor_MapsTheQualityBands(
        AudioFormat format, string configured, string? expected)
        => Assert.That(QobuzDownloader.QualityFor(DownloadQuality.Any with { Format = format },
            configured), Is.EqualTo(expected));

    [TestCase("6", "6")]
    [TestCase("27", "27")]
    [TestCase(" 7 ", "7")]
    [TestCase("", "6")]
    [TestCase("99", "6")]
    [TestCase("nonsense", "6")]
    public void NormalizeQuality_FallsBackToCdQuality(string input, string expected)
        => Assert.That(QobuzDownloader.NormalizeQuality(input), Is.EqualTo(expected));

    [Test]
    public void ReadTokenFile_ReadsTheStoredToken_AndHandlesTheEdges()
    {
        var configFolder = Path.Combine(NewDir(), "qobuz-dl");
        Assert.That(QobuzDownloader.ReadTokenFile(configFolder), Is.Null, "no qobuz-dlp config yet");

        Directory.CreateDirectory(configFolder);
        File.WriteAllText(Path.Combine(configFolder, ".qobuz_dl.oauth.txt"), "  token-abc  \n");
        Assert.That(QobuzDownloader.ReadTokenFile(configFolder), Is.EqualTo("token-abc"));

        File.WriteAllText(Path.Combine(configFolder, ".qobuz_dl.oauth.txt"), "   ");
        Assert.That(QobuzDownloader.ReadTokenFile(configFolder), Is.Null, "an empty file is no token");
    }

    // ── Availability ─────────────────────────────────────────────────────

    [Test]
    public async Task IsAvailable_MissingCommand_False()
    {
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", "definitely-not-a-real-command-xyz");
        Assert.That(await Create(config).IsAvailableAsync(), Is.False);
    }

    [Test]
    public async Task IsAvailable_BlockedAccount_SetsTheBlockedAuthStatus()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("bundle.js")) return BundleRespond(request);
            if (path.Contains("track/search"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"status":"error","code":403,"message":"Account is blocked","error_code":"USER_BLOCKED"}""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            if (path.Contains("login") && request.Method == HttpMethod.Post)
                return Text(
                    """{"user":{"credential":{"parameters":{"short_label":"Studio"}}}}""",
                    "application/json");
            return BundleRespond(request);
        };

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://play.qobuz.com") };
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", CliStub.Producing(NewDir()));
        config.Set("qobuz", "user_auth_token", "token-xyz");
        var plugin = new QobuzDownloader(
            new QobuzClient(http, NullLogger<QobuzClient>.Instance),
            config, NullLogger<QobuzDownloader>.Instance, NewDir());

        var available = await plugin.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(plugin.AuthStatus.State, Is.EqualTo(PluginAuthState.Blocked),
                "a blocked account is an auth problem, not a dead service");
            Assert.That(plugin.LastUnavailableReason, Does.Contain("USER_BLOCKED"));
        });
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task Download_RunsTheCli_MovesAndTagsTheFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var output = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);
        var plugin = Create(config, NewDir());

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", output, "mb-qobuz-1",
            new DownloadQuality(AudioFormat.Flac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".flac"));

            var args = CliStub.RecordedArguments(script);
            Assert.That(args, Does.Contain("dl"));
            Assert.That(args, Does.Contain("--directory"));
            Assert.That(args, Does.Contain("--quality 6"));
            Assert.That(args, Does.Contain("--no-m3u"));
            Assert.That(args, Does.Contain("--no-db"));
            Assert.That(args, Does.Contain("https://play.qobuz.com/track/5966783"));

            Assert.That(Directory.GetDirectories(output), Is.Empty,
                "the hidden per-track folder is cleaned up");
        });
    }

    [Test]
    public async Task Download_HiResQuality_UsesTheConfiguredTier()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);
        config.Set("qobuz", "quality", "27");

        var result = await Create(config, NewDir()).DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-hires",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(CliStub.RecordedArguments(script), Does.Contain("--quality 27"));
    }

    [Test]
    public async Task Download_CliFails_ReportsTheStderr()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("qobuz", "command",
            CliStub.Producing(toolDir, exitCode: 1, stderr: "Login expired"));
        var plugin = Create(config, NewDir());

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-fail");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Login expired"));
        });
    }

    [Test]
    public async Task Download_NoQualityFits_FailsWithoutRunningTheCli()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);

        var result = await Create(config).DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-aac",
            new DownloadQuality(AudioFormat.Aac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("no qobuz-dlp quality fits"));
            Assert.That(CliStub.RecordedArguments(script), Is.Empty, "the CLI never ran");
        });
    }

    [Test]
    public async Task Download_SeedsTheQobuzDlpConfigAndToken()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var home = NewDir();
        var toolDir = NewDir();
        var script = CliStub.Custom(toolDir, "cli.sh",
            """
            rec="$(dirname "$0")/record.txt"
            echo "home=$HOME" > "$rec"
            cat "$HOME/.config/qobuz-dl/config.ini" >> "$rec"
            echo "---token---" >> "$rec"
            cat "$HOME/.config/qobuz-dl/.qobuz_dl.oauth.txt" >> "$rec"
            dir=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--directory" ]; then dir="$a"; fi
              prev="$a"
            done
            mkdir -p "$dir"
            printf 'fLaC' > "$dir/Artist - Song.flac"
            exit 0
            """);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);
        config.Set("qobuz", "user_auth_token", "token-xyz");
        var plugin = Create(config, home);

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-seed");

        var record = File.ReadAllText(Path.Combine(toolDir, "record.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(record, Does.Contain($"home={home}"),
                "the CLI must read the config folder the plugin manages");
            Assert.That(record, Does.Contain("app_id = 798273057"),
                "the scraped app id reaches the CLI config");
            Assert.That(record, Does.Contain("private_key = c8f4b3a2d1"));
            Assert.That(record, Does.Contain("secrets = "));
            Assert.That(record, Does.Contain("---token---"));
            Assert.That(record, Does.Contain("token-xyz"),
                "the plugin token reaches the CLI's token file");
        });
    }

    [Test]
    public async Task Download_InvalidUrl_Fails()
    {
        var result = await Create().DownloadAsync(
            "https://example.com/not-a-track", NewDir(), "mb-bad");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not a Qobuz track URL"));
        });
    }
}
