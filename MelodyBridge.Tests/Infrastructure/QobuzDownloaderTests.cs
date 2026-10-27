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
    public async Task Download_CountryBlockedTrack_IsNotAnAccountBlock()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("qobuz", "command",
            CliStub.Producing(toolDir, exitCode: 1,
                stderr: "ERROR: This track is geo-blocked in your country"));
        var plugin = Create(config, NewDir());

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-geo");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(plugin.AuthStatus.State, Is.EqualTo(PluginAuthState.Unknown),
                "geo-blocking a track is not blocking the account");
        });
    }

    [Test]
    public async Task Download_UserBlockedCliFailure_FlagsTheAccount()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("qobuz", "command",
            CliStub.Producing(toolDir, exitCode: 1,
                stderr: "ERROR: Account is blocked (USER_BLOCKED)"));
        var plugin = Create(config, NewDir());

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-user-blocked");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(plugin.AuthStatus.State, Is.EqualTo(PluginAuthState.Blocked));
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
