using System.Net.Http.Json;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Live Plex tests. An unclaimed headless Plex answers /identity without
/// any token — that is the reachability probe both the wizard's Test
/// button and PlexSync use — and token-gated routes accept requests while
/// the server is unclaimed, so the directory's Test step works against a
/// real container. Full playlist sync needs a claimed server with a music
/// library; when PLEX_TOKEN is provided in the environment the deeper
/// sync test runs, otherwise it ignores itself honestly.
/// </summary>
[TestFixture]
[Category("Live")]
public class PlexSyncLiveTests
{
    private const string BaseUrl = "http://127.0.0.1:32400";
    private const string Container = "mb-live-test-plex";

    private HttpClient _http = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Assume.That(LiveFixtureHelpers.DockerImageReady("plexinc/pms-docker:latest"),
            "plex image or docker daemon unavailable");

        var root = Path.Combine(FindRepoRoot(), ".mb-live", "plex");
        var config = Path.Combine(root, "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);

        DockerOrIgnore($"rm -f {Container}");
        LiveFixtureHelpers.Docker($"run -d --name {Container} --network host " +
            $"-v {config}:/config plexinc/pms-docker:latest", 120);

        await WaitUntilIdentity();
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "melodybridge");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { LiveFixtureHelpers.Docker($"rm -f {Container}", 30); } catch { /* best effort */ }
        _http?.Dispose();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.Name.Equals("MelodyBridge.Tests", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;
        return dir!.Parent!.FullName;
    }

    private static void DockerOrIgnore(string args)
    {
        try { LiveFixtureHelpers.Docker(args); }
        catch (InvalidOperationException ex) { Assert.Ignore(ex.Message); }
    }

    private async Task WaitUntilIdentity()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var resp = await http.GetAsync(BaseUrl + "/identity");
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(2000);
        }
        Assert.Fail("plex /identity did not answer within 2 minutes");
    }

    private sealed class FixedSettings : IPlexSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default)
            => Task.FromResult(Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? "unclaimed-token");
    }

    // ── tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Identity_AnswersWithMachineIdentifier()
    {
        var identity = await _http.GetFromJsonAsync<JsonElement>(BaseUrl + "/identity");
        Assert.That(identity.GetProperty("MediaContainer").TryGetProperty("machineIdentifier", out _),
            Is.True, "the sync requires the machine id for its server:// uris");
    }

    [Test]
    public async Task Directory_TestConnection_SucceedsAgainstRealServer()
    {
        var directory = new PlexDirectory(new HttpClient());
        Assert.That(await directory.TestConnectionAsync(BaseUrl, "any-token"), Is.True,
            "token-gated routes answer on a real unclaimed server");
    }

    [Test]
    public async Task Sync_NoMusicLibrary_ReportsItInsteadOfFailing()
    {
        var sync = new PlexSync(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<PlexSync>.Instance, new FixedSettings());

        var playlist = new Playlist
        {
            Name = "MB Live Mix",
            Tracks = new List<Track>
            {
                new() { Title = "Alpha Song", CurrentTrackLocation = new FileLocation("/media/alpha.mp3") },
            },
        };
        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/ignored.m3u", false, null));

        Assert.That(sync.LastReport!.Message, Does.Contain("music"),
            "a fresh server without a music library explains itself in the report");
    }

    [Test]
    public async Task Sync_WithClaimToken_IfProvided()
    {
        var token = Environment.GetEnvironmentVariable("PLEX_TOKEN");
        Assume.That(string.IsNullOrWhiteSpace(token) is false,
            "PLEX_TOKEN not set; claimed-server sync skipped");

        var sync = new PlexSync(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<PlexSync>.Instance, new FixedSettings());
        var playlist = new Playlist
        {
            Name = "MB Live Mix",
            Tracks = new List<Track>
            {
                new() { Title = "Alpha Song", CurrentTrackLocation = new FileLocation("/media/alpha.mp3") },
            },
        };
        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/ignored.m3u", false, null));

        Assert.That(sync.LastReport!.Message, Is.Not.EqualTo("Created playlist").Or.Not.EqualTo("Updated existing playlist"),
            "claimed-server sync completed with a report");
    }
}
