using System.Net;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Tidal;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Tidal plugin: tiddl's stored session for the login, Tidal's v1
/// search for the ranking, the user's own tiddl CLI (a POSIX shell stub
/// that writes real audio) for the download. No Tidal account needed.
/// </summary>
[TestFixture]
public class TidalDownloaderTests
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
        var dir = Path.Combine(Path.GetTempPath(), $"mb-tidal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>An app folder holding a tiddl session, the way tiddl writes it.</summary>
    private string NewAppPath(string token = "tidal-token-abc", long expiresAt = 0, string country = "NL")
    {
        var dir = NewDir();
        expiresAt = expiresAt == 0
            ? DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeSeconds()
            : expiresAt;
        File.WriteAllText(Path.Combine(dir, "auth.json"), JsonSerializer.Serialize(new
        {
            token,
            refresh_token = "refresh-xyz",
            expires_at = expiresAt,
            user_id = "12345",
            country_code = country,
        }));
        return dir;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Respond(request));
    }

    private static TidalDownloader Create(
        StubPluginConfig? config = null, StubHttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new StubHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.tidal.com"),
        };
        return new TidalDownloader(
            new TidalClient(http, NullLogger<TidalClient>.Instance),
            config ?? new StubPluginConfig(),
            NullLogger<TidalDownloader>.Instance);
    }

    private static TidalClient.TidalTrack Track(
        long id, string title, string artist, int duration, string? isrc = null, bool ready = true)
        => new(id, title, artist, TimeSpan.FromSeconds(duration), isrc, ready);

    // ── The stored session ───────────────────────────────────────────────

    [Test]
    public void TidalAuth_ReadsTheStoredSession()
    {
        var appPath = NewAppPath();
        var session = TidalAuth.Read(appPath);

        Assert.Multiple(() =>
        {
            Assert.That(session, Is.Not.Null);
            Assert.That(session!.Token, Is.EqualTo("tidal-token-abc"));
            Assert.That(session.CountryCode, Is.EqualTo("NL"));
            Assert.That(session.UserId, Is.EqualTo("12345"));
            Assert.That(session.HasRefreshToken, Is.True);
            Assert.That(session.Expired, Is.False);
        });
    }

    [Test]
    public void TidalAuth_MissingFile_IsNull()
        => Assert.That(TidalAuth.Read(NewDir()), Is.Null, "no login is not an exception");

    [Test]
    public void TidalAuth_UnreadableJson_IsNull()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "auth.json"), "{not json");
        Assert.That(TidalAuth.Read(dir), Is.Null, "a half-written auth file must not crash the waterfall");
    }

    [Test]
    public void TidalAuth_PastExpiry_IsExpired()
    {
        var appPath = NewAppPath(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
        Assert.That(TidalAuth.Read(appPath)!.Expired, Is.True);
    }

    [Test]
    public void TidalAuth_ResolveAppPath_PrefersTheSetting()
    {
        Assert.That(TidalAuth.ResolveAppPath("/tmp/custom-tiddl"), Is.EqualTo("/tmp/custom-tiddl"));
        Assert.That(TidalAuth.ResolveAppPath("  "), Does.EndWith(".tiddl"));
    }

    // ── Parsing the search payload ───────────────────────────────────────

    [Test]
    public void ParseSearch_ReadsTheRealPayloadShape()
    {
        using var doc = JsonDocument.Parse("""
        {
          "tracks": {
            "items": [
              {
                "id": 12345,
                "title": "Around the World",
                "duration": 429,
                "isrc": "GBDUW0600009",
                "streamReady": true,
                "artists": [ { "id": 1, "name": "Daft Punk", "type": "MAIN" } ],
                "artist": { "id": 1, "name": "Daft Punk", "type": "MAIN" }
              },
              {
                "id": 999,
                "title": "Around the World (Live)",
                "duration": 300,
                "isrc": "",
                "streamReady": false,
                "artists": [ { "id": 2, "name": "Tribute Band", "type": "MAIN" } ]
              }
            ]
          }
        }
        """);

        var tracks = TidalClient.ParseSearch(doc.RootElement);

        Assert.That(tracks, Has.Count.EqualTo(2), "unplayable entries are still reported; ranking drops them");
        Assert.Multiple(() =>
        {
            Assert.That(tracks[0].Id, Is.EqualTo(12345));
            Assert.That(tracks[0].Title, Is.EqualTo("Around the World"));
            Assert.That(tracks[0].Artist, Is.EqualTo("Daft Punk"));
            Assert.That(tracks[0].Duration, Is.EqualTo(TimeSpan.FromSeconds(429)));
            Assert.That(tracks[0].Isrc, Is.EqualTo("GBDUW0600009"));
            Assert.That(tracks[0].StreamReady, Is.True);
            Assert.That(tracks[1].StreamReady, Is.False);
        });
    }

    [Test]
    public void ParseSearch_EmptyOrUnknownShape_IsEmpty()
    {
        using var doc = JsonDocument.Parse("{\"tracks\":{}}");
        Assert.That(TidalClient.ParseSearch(doc.RootElement), Is.Empty);
    }

    // ── Ranking ──────────────────────────────────────────────────────────

    [Test]
    public void Rank_PicksTheRealMatch_NotTheFirstItem()
    {
        var hit = TidalDownloader.Rank(new[]
        {
            Track(111, "Around the World (Cover)", "Some Band", 240),
            Track(222, "Around the World", "Daft Punk", 429, "GBDUW0600009"),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/222"));
            Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
        });
    }

    [Test]
    public void Rank_ExactIsrc_AcceptsADifferentlyWordedTitle()
    {
        // Tidal titles the same recording without the requested marker.
        // The code is the identity, so the title gate must not veto it.
        var hit = TidalDownloader.Rank(new[]
        {
            Track(222, "The Last Airbender", "SLVL", 217, "USRC17607839"),
        }, new TrackQuery("SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/222"));
    }

    [Test]
    public void Rank_ExactIsrc_WinsOverACloserTitle()
    {
        var hit = TidalDownloader.Rank(new[]
        {
            Track(111, "Around the World", "Daft Punk", 429, "OTHERISRC001"),
            Track(222, "Around the World (Remastered)", "Daft Punk", 430, "GBDUW0600009"),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429), Isrc: "GBDUW0600009"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/222"));
    }

    [Test]
    public void Rank_UnplayableTracks_AreDropped()
    {
        var hit = TidalDownloader.Rank(new[]
        {
            Track(111, "Around the World", "Daft Punk", 429, "GBDUW0600009", ready: false),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Null, "a track Tidal will not stream must never win");
    }

    [Test]
    public void Rank_Empty_IsNull()
        => Assert.That(TidalDownloader.Rank(
            Array.Empty<TidalClient.TidalTrack>(),
            new TrackQuery("A", "T", DownloadQuality.Any)), Is.Null);

    // ── Mapping helpers ──────────────────────────────────────────────────

    [TestCase(AudioFormat.Flac, "high", "high")]
    [TestCase(AudioFormat.Flac, "max", "max")]
    [TestCase(AudioFormat.Aac, "high", "normal")]
    [TestCase(AudioFormat.Mp3, "high", null)]
    [TestCase(AudioFormat.Auto, "max", "max")]
    public void QualityFor_MapsTheQualityBands(
        AudioFormat format, string configured, string? expected)
        => Assert.That(TidalDownloader.QualityFor(DownloadQuality.Any with { Format = format },
            configured), Is.EqualTo(expected));

    [TestCase("high", "high")]
    [TestCase("max", "max")]
    [TestCase("  MAX ", "max")]
    [TestCase("", "high")]
    [TestCase("99", "high")]
    [TestCase("losless", "high")]
    public void NormalizeQuality_FallsBackToCdQuality(string input, string expected)
        => Assert.That(TidalDownloader.NormalizeQuality(input), Is.EqualTo(expected));

    [TestCase("https://tidal.com/browse/track/12345", true, 12345)]
    [TestCase("https://listen.tidal.com/track/12345?utm=1", true, 12345)]
    [TestCase("track/12345", true, 12345)]
    [TestCase("12345", true, 12345)]
    [TestCase("https://open.spotify.com/track/abc", false, 0)]
    [TestCase("", false, 0)]
    public void TryExtractTrackId_HandlesUrlsAndIds(string input, bool expected, long expectedId)
    {
        var ok = TidalDownloader.TryExtractTrackId(input, out var id);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.EqualTo(expected));
            Assert.That(id, Is.EqualTo(expectedId));
        });
    }

    // ── Availability ─────────────────────────────────────────────────────

    [Test]
    public async Task IsAvailable_MissingCommand_False()
    {
        var config = new StubPluginConfig();
        config.Set("tidal", "command", "definitely-not-a-real-command-xyz");
        var downloader = Create(config);

        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(downloader.LastUnavailableReason, Does.Contain("tiddl"));
        });
    }

    [Test]
    public async Task IsAvailable_NoSession_ExplainsTheLogin()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path", NewDir());

        var downloader = Create(config);
        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(downloader.LastUnavailableReason, Does.Contain("tiddl auth login"));
        });
    }

    [Test]
    public async Task IsAvailable_ExpiredSession_ExplainsTheRefresh()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path",
            NewAppPath(expiresAt: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()));

        var downloader = Create(config);
        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(downloader.LastUnavailableReason, Does.Contain("expired"));
        });
    }

    [Test]
    public async Task IsAvailable_LoggedInSession_True()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path", NewAppPath());

        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tracks\":{\"items\":[]}}"),
            },
        };
        var downloader = Create(config, handler);
        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True);
            Assert.That(downloader.LastUnavailableReason, Is.Null);
        });
    }

    // ── Search over HTTP ─────────────────────────────────────────────────

    [Test]
    public async Task Search_SendsTheStoredTokenAndParsesTheHit()
    {
        var appPath = NewAppPath(token: "token-xyz", country: "NL");
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            Assert.That(request.RequestUri!.ToString(), Does.Contain("api.tidal.com/v1/search"));
            Assert.That(request.RequestUri!.ToString(), Does.Contain("countryCode=NL"));
            Assert.That(request.Headers.GetValues("Authorization").Single(),
                Is.EqualTo("Bearer token-xyz"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"tracks":{"items":[{"id":222,"title":"Around the World","duration":429,
                "isrc":"GBDUW0600009","streamReady":true,
                "artists":[{"id":1,"name":"Daft Punk","type":"MAIN"}]}]}}
                """, System.Text.Encoding.UTF8, "application/json"),
            };
        };
        var config = new StubPluginConfig();
        config.Set("tidal", "app_path", appPath);

        var hit = await Create(config, handler).SearchAsync(
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any,
                ExpectedDuration: TimeSpan.FromSeconds(429)));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://tidal.com/browse/track/222"));
    }

    [Test]
    public async Task Search_ExpiredSession_ReturnsNullWithoutARequest()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ =>
            {
                Assert.Fail("an expired session must not be sent to the API");
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var config = new StubPluginConfig();
        config.Set("tidal", "app_path",
            NewAppPath(expiresAt: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()));

        var hit = await Create(config, handler).SearchAsync(
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Null);
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task Download_RunsTiddl_MovesAndTagsTheFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var output = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("tidal", "command", script);
        config.Set("tidal", "app_path", NewAppPath());

        var result = await Create(config).DownloadAsync(
            "https://tidal.com/browse/track/12345", output, "mb-tidal-1",
            new DownloadQuality(AudioFormat.Flac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".flac"));

            var args = CliStub.RecordedArguments(script);
            Assert.That(args, Does.Contain("download"));
            Assert.That(args, Does.Contain("--track-quality high"));
            Assert.That(args, Does.Contain("--path "));
            Assert.That(args, Does.Contain("--scan-path "));
            Assert.That(args, Does.Contain("--no-skip"));
            Assert.That(args, Does.Contain("url"));
            Assert.That(args, Does.Contain("https://tidal.com/browse/track/12345"));
            Assert.That(args, Does.Contain("mb-tidal-1"), "the per-track folder");

            Assert.That(Directory.GetDirectories(output), Is.Empty,
                "the private per-track folder is cleaned up");
        });
    }

    [Test]
    public async Task Download_HiResQuality_UsesTheConfiguredTier()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("tidal", "command", script);
        config.Set("tidal", "app_path", NewAppPath());
        config.Set("tidal", "quality", "max");

        var result = await Create(config).DownloadAsync(
            "https://tidal.com/browse/track/12345", NewDir(), "mb-hires",
            new DownloadQuality(AudioFormat.Flac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(CliStub.RecordedArguments(script), Does.Contain("--track-quality max"));
        });
    }

    [Test]
    public async Task Download_Mp3Request_FailsWithoutRunningTheCli()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("tidal", "command", script);
        config.Set("tidal", "app_path", NewAppPath());

        var result = await Create(config).DownloadAsync(
            "https://tidal.com/browse/track/12345", NewDir(), "mb-mp3",
            new DownloadQuality(AudioFormat.Mp3));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("no tiddl quality fits"));
            Assert.That(CliStub.RecordedArguments(script), Is.Empty, "the CLI never ran");
        });
    }

    [Test]
    public async Task Download_CustomAppPath_PointsTiddlAtIt()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var appPath = NewAppPath();
        var script = CliStub.Custom(toolDir, "cli.sh",
            """
            echo "path=$TIDDL_PATH" > "$(dirname "$0")/record.txt"
            dir=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--path" ]; then dir="$a"; fi
              prev="$a"
            done
            mkdir -p "$dir"
            printf 'fLaC' > "$dir/Artist - Song.flac"
            exit 0
            """);
        var config = new StubPluginConfig();
        config.Set("tidal", "command", script);
        config.Set("tidal", "app_path", appPath);

        var result = await Create(config).DownloadAsync(
            "https://tidal.com/browse/track/12345", NewDir(), "mb-apppath");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(File.ReadAllText(Path.Combine(toolDir, "record.txt")).Trim(),
                Is.EqualTo($"path={appPath}"), "a custom app folder travels as TIDDL_PATH");
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
            Assert.That(result.ErrorMessage, Does.Contain("not a Tidal track URL"));
        });
    }

    // ── Pasted tokens ────────────────────────────────────────────────────

    private static string MakeJwt(Dictionary<string, object> claims)
    {
        static string Part(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Part(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Part(JsonSerializer.SerializeToUtf8Bytes(claims));
        return $"{header}.{payload}.signature";
    }

    [Test]
    public void ParseSettings_RawJwt_DerivesTheClaims()
    {
        var jwt = MakeJwt(new Dictionary<string, object>
        {
            ["exp"] = 1893456000L,
            ["userId"] = 777,
            ["countryCode"] = "nl",
        });

        var data = TidalAuth.ParseSettings(jwt, "", "", "");

        Assert.That(data, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(data!.Token, Is.EqualTo(jwt));
            Assert.That(data.ExpiresAt, Is.EqualTo(1893456000));
            Assert.That(data.UserId, Is.EqualTo("777"));
            Assert.That(data.CountryCode, Is.EqualTo("NL"));
            Assert.That(data.RefreshToken, Is.Null, "a browser token carries no refresh token");
        });
    }

    [Test]
    public void ParseSettings_FullAuthFile_ReadsEveryField()
    {
        var data = TidalAuth.ParseSettings(
            """{"token":"tok-1","refresh_token":"ref-1","expires_at":1234,"user_id":42,"country_code":"de"}""",
            "", "", "");

        Assert.That(data, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(data!.Token, Is.EqualTo("tok-1"));
            Assert.That(data.RefreshToken, Is.EqualTo("ref-1"));
            Assert.That(data.ExpiresAt, Is.EqualTo(1234));
            Assert.That(data.UserId, Is.EqualTo("42"));
            Assert.That(data.CountryCode, Is.EqualTo("DE"));
        });
    }

    [Test]
    public void ParseSettings_AdvancedFieldsWinOverTheToken()
    {
        var jwt = MakeJwt(new Dictionary<string, object>
        {
            ["exp"] = 111,
            ["uid"] = "from-jwt",
            ["country"] = "FR",
        });

        var data = TidalAuth.ParseSettings(jwt, "plain-refresh", "advanced-user", "be");

        Assert.That(data, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(data!.RefreshToken, Is.EqualTo("plain-refresh"));
            Assert.That(data.UserId, Is.EqualTo("advanced-user"));
            Assert.That(data.CountryCode, Is.EqualTo("BE"));
            Assert.That(data.ExpiresAt, Is.EqualTo(111));
        });
    }

    [Test]
    public void ParseSettings_UnknownShapes_AreNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TidalAuth.ParseSettings("{not json", "", "", ""), Is.Null);
            Assert.That(TidalAuth.ParseSettings("   ", "", "", ""), Is.Null);
            Assert.That(TidalAuth.ParseSettings("""{"refresh_token":"ref"}""", "", "", ""), Is.Null,
                "a JSON without a token is not a session");
        });
    }

    [Test]
    public void TidalAuth_Seed_WritesTheAuthFile()
    {
        var appPath = NewDir();

        var written = TidalAuth.Seed(appPath, new TidalAuth.AuthData(
            "tok-1", null, 1893456000, "42", "NL"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(appPath, "auth.json")));
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.True);
            Assert.That(root.GetProperty("token").GetString(), Is.EqualTo("tok-1"));
            Assert.That(root.GetProperty("refresh_token").GetString(),
                Is.EqualTo(TidalAuth.NoRefreshPlaceholder), "tiddl refuses to run without one");
            Assert.That(root.GetProperty("expires_at").GetInt64(), Is.EqualTo(1893456000));
            Assert.That(root.GetProperty("user_id").GetString(), Is.EqualTo("42"));
            Assert.That(root.GetProperty("country_code").GetString(), Is.EqualTo("NL"));
        });
    }

    [Test]
    public void TidalAuth_Seed_SameSettings_LeavesTiddlsRenewalAlone()
    {
        var appPath = NewDir();
        var data = new TidalAuth.AuthData("tok-1", "ref-1", 123, "42", "NL");
        Assert.That(TidalAuth.Seed(appPath, data), Is.True);

        // tiddl refreshed the session itself: same refresh token, new access token.
        File.WriteAllText(Path.Combine(appPath, "auth.json"),
            """{"token":"renewed-by-tiddl","refresh_token":"ref-1","expires_at":999,"user_id":"42","country_code":"NL"}""");

        Assert.That(TidalAuth.Seed(appPath, data), Is.False,
            "the same settings must not overwrite a session tiddl renewed");
        Assert.That(TidalAuth.Read(appPath)!.Token, Is.EqualTo("renewed-by-tiddl"));
    }

    [Test]
    public void TidalAuth_Seed_KeepsWhatTheFileAlreadyHas()
    {
        var appPath = NewDir();
        TidalAuth.Seed(appPath, new TidalAuth.AuthData("old-token", "real-refresh", 111, "42", "NL"));

        TidalAuth.Seed(appPath, new TidalAuth.AuthData("new-token", null, 222));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(appPath, "auth.json")));
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("token").GetString(), Is.EqualTo("new-token"));
            Assert.That(root.GetProperty("refresh_token").GetString(), Is.EqualTo("real-refresh"),
                "a real refresh token survives a token-only paste");
            Assert.That(root.GetProperty("user_id").GetString(), Is.EqualTo("42"));
            Assert.That(root.GetProperty("country_code").GetString(), Is.EqualTo("NL"));
        });
    }

    [Test]
    public async Task IsAvailable_PastedToken_IsReadyWithoutTiddlLogin()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var appPath = NewDir();
        var jwt = MakeJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds(),
            ["userId"] = 777,
            ["countryCode"] = "NL",
        });
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path", appPath);
        config.Set("tidal", "token", jwt);

        var handler = new StubHttpMessageHandler
        {
            Respond = request =>
            {
                Assert.That(request.Headers.GetValues("Authorization").Single(),
                    Is.EqualTo($"Bearer {jwt}"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tracks\":{\"items\":[]}}"),
                };
            },
        };
        var downloader = Create(config, handler);

        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True, downloader.LastUnavailableReason);
            Assert.That(File.Exists(Path.Combine(appPath, "auth.json")), Is.True,
                "the pasted token must reach tiddl's own auth file");
        });
    }

    [Test]
    public async Task IsAvailable_RejectedToken_Explains()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path", NewDir());
        config.Set("tidal", "token", MakeJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds(),
            ["userId"] = 777,
            ["countryCode"] = "NL",
        }));

        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        var downloader = Create(config, handler);

        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(downloader.LastUnavailableReason, Does.Contain("rejected"));
        });
    }

    [Test]
    public async Task IsAvailable_TokenWithoutUserOrCountry_Explains()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path", NewDir());
        config.Set("tidal", "token", MakeJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds(),
        }));

        var downloader = Create(config);
        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(downloader.LastUnavailableReason, Does.Contain("user id"));
        });
    }

    [Test]
    public async Task IsAvailable_UnreadableToken_Explains()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Silent(toolDir));
        config.Set("tidal", "app_path", NewDir());
        config.Set("tidal", "token", "{half a json");

        var downloader = Create(config);
        var available = await downloader.IsAvailableAsync();

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.False);
            Assert.That(downloader.LastUnavailableReason, Does.Contain("not readable"));
        });
    }

    [Test]
    public async Task Download_PastedToken_SeedsTiddlsAuthFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var appPath = NewDir();
        var expiry = DateTimeOffset.UtcNow.AddHours(10).ToUnixTimeSeconds();
        var jwt = MakeJwt(new Dictionary<string, object>
        {
            ["exp"] = expiry,
            ["uid"] = 777,
            ["countryCode"] = "NL",
        });
        var config = new StubPluginConfig();
        config.Set("tidal", "command", CliStub.Producing(toolDir));
        config.Set("tidal", "app_path", appPath);
        config.Set("tidal", "token", jwt);

        var result = await Create(config).DownloadAsync(
            "https://tidal.com/browse/track/12345", NewDir(), "mb-pasted-token",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(appPath, "auth.json")));
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("token").GetString(), Is.EqualTo(jwt));
            Assert.That(root.GetProperty("user_id").GetString(), Is.EqualTo("777"));
            Assert.That(root.GetProperty("country_code").GetString(), Is.EqualTo("NL"));
            Assert.That(root.GetProperty("expires_at").GetInt64(), Is.EqualTo(expiry));
            Assert.That(root.GetProperty("refresh_token").GetString(),
                Is.EqualTo(TidalAuth.NoRefreshPlaceholder));
        });
    }
}
