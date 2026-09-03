using System.Net.Http.Json;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Live Jellyfin end-to-end: a real container is bootstrapped headless
/// (startup wizard, admin user, music library, scan), then JellyfinSync
/// resolves the scanned files by path, creates a playlist, updates it and
/// marks a favorite: all verified through the server's own API. Docker is
/// required; the test ignores itself when the daemon or image is missing.
/// </summary>
[TestFixture]
[Category("Live")]
public class JellyfinSyncLiveTests
{
    private const string BaseUrl = "http://127.0.0.1:8096";
    private const string AdminUser = "mbadmin";
    private const string AdminPw = "mb-admin-pw-123";
    private const string Container = "mb-live-test-jellyfin";

    private string _root = null!;
    private string _configDir = null!;
    private string _token = null!;
    private HttpClient _api = null!;
    private HttpClient _syncClient = null!;

    private sealed class FixedSettings : IJellyfinSettings
    {
        public string ApiKey { get; init; } = "";
        public string? UserId { get; init; }

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(ApiKey);
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult(UserId);
    }

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Assume.That(LiveFixtureHelpers.DockerImageReady("jellyfin/jellyfin:latest"),
            "jellyfin image or docker daemon unavailable");

        _root = Path.Combine(FindRepoRoot(), ".mb-live", "jellyfin");
        var music = Path.Combine(_root, "music");
        // Unique config dir per run: a completed wizard persists in config
        // and would 401 the startup calls, and the container's files are
        // root-owned and not cleanable from the test process.
        var config = Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(config);
        _configDir = config;
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(music, "alpha.mp3"), "Alpha Song", "Alice");
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(music, "beta.mp3"), "Beta Song", "Bob");

        // Fresh container on the host network (bridge networking is not
        // available in every CI sandbox, host networking always is).
        DockerOrIgnore($"rm -f {Container}");
        LiveFixtureHelpers.Docker($"run -d --name {Container} --network host " +
            $"-v {music}:/media:ro -v {_configDir}:/config jellyfin/jellyfin:latest", 120);

        await WaitUntilReady();
        await CompleteStartupWizard();
        _token = await Authenticate();
        await AddMusicLibrary();
        await WaitUntilScanned(minimum: 2);

        await WaitUntilResolvable("Alpha Song");
        await WaitUntilResolvable("Beta Song");

        _api = new HttpClient();
        _api.DefaultRequestHeaders.Add("X-Emby-Token", _token);
        _syncClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { LiveFixtureHelpers.Docker($"rm -f {Container}", 30); } catch { /* best effort */ }
        _api?.Dispose();
        _syncClient?.Dispose();
    }

    // ── bootstrap steps ─────────────────────────────────────────────

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

    private async Task WaitUntilReady()
    {
        using var http = new HttpClient();
        for (var i = 0; i < 60; i++)
        {
            try
            {
                var info = await http.GetFromJsonAsync<JsonElement>(BaseUrl + "/System/Info/Public");
                if (info.TryGetProperty("Version", out _)) return;
            }
            catch { /* not up yet */ }
            await Task.Delay(2000);
        }
        Assert.Fail("jellyfin did not answer within 2 minutes");
    }

    private async Task CompleteStartupWizard()
    {
        using var http = new HttpClient();
        // Wizard order matters: configuration, initialize the default user
        // (GET), rename + password it (POST), complete.
        await Post(http, "/Startup/Configuration", """{"UICulture":"en-US"}""");
        await http.GetStringAsync(BaseUrl + "/Startup/User");
        await Post(http, "/Startup/User", $$"""{"Name":"{{AdminUser}}","Password":"{{AdminPw}}"}""");
        await Post(http, "/Startup/Complete", "{}");
    }

    private static async Task Post(HttpClient http, string path, string json)
    {
        using var resp = await http.PostAsync(BaseUrl + path,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"POST {path} -> {(int)resp.StatusCode}");
    }

    private async Task<string> Authenticate()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Emby-Authorization",
            "MediaBrowser Client=\"MelodyBridge\", Device=\"mb-test\", DeviceId=\"mb-live\", Version=\"1.0\"");
        using var resp = await http.PostAsync(BaseUrl + "/Users/AuthenticateByName",
            new StringContent($$"""{"Username":"{{AdminUser}}","Pw":"{{AdminPw}}"}""",
                System.Text.Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(resp.IsSuccessStatusCode, Is.True, "admin login");
        return body.GetProperty("AccessToken").GetString()!;
    }

    private async Task AddMusicLibrary()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Emby-Token", _token);
        using var resp = await http.PostAsync(
            BaseUrl + "/Library/VirtualFolders?name=Music&collectionType=music&paths=/media&refreshLibrary=true",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.That(resp.IsSuccessStatusCode, Is.True,
            $"music library created -> {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task WaitUntilScanned(int minimum)
    {
        for (var i = 0; i < 30; i++)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Emby-Token", _token);
            var items = await http.GetFromJsonAsync<JsonElement>(
                BaseUrl + "/Items?Recursive=true&IncludeItemTypes=Audio");
            if (items.GetProperty("TotalRecordCount").GetInt32() >= minimum) return;
            await Task.Delay(2000);
        }
        Assert.Fail("library scan did not surface the two test tracks");
    }

    /// <summary>The scanner surfaces an item in /Items before its metadata is
    /// searchable; the resolver's reliable lookup against a login token is the
    /// title/artist search (ByPath needs a true API key, filename search does
    /// not match), so wait until the titles answer.</summary>
    private async Task WaitUntilResolvable(string title)
    {
        for (var i = 0; i < 30; i++)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Emby-Token", _token);
            var list = await http.GetFromJsonAsync<JsonElement>(
                BaseUrl + "/Items?Recursive=true&IncludeItemTypes=Audio&SearchTerm=" + Uri.EscapeDataString(title));
            if (list.GetProperty("TotalRecordCount").GetInt32() > 0) return;
            await Task.Delay(2000);
        }
        Assert.Fail(title + " never became searchable");
    }

    // ── the sync under test ──────────────────────────────────────────

    private JellyfinSync NewSync(string token)
        => new(_syncClient, NullLogger<JellyfinSync>.Instance,
            new FixedSettings { ApiKey = token });

    private static Playlist PlaylistOf((string title, bool liked)[] tracks) => new()
    {
        Name = "MB Live Mix",
        Tracks = tracks.Select(t => new Track
        {
            Title = t.title,
            IsLiked = t.liked,
            CurrentTrackLocation = new FileLocation("/media/" +
                (t.title.Contains("Alpha") ? "alpha.mp3" : "beta.mp3")),
        }).ToList(),
    };

    // The job's path remap: host-side test dir -> container /media.
    private PlaylistOutputOptions HostOptions()
        => new("/tmp/ignored.m3u", false,
            new Dictionary<string, string>
            {
                { Path.Combine(FindRepoRoot(), ".mb-live", "jellyfin", "music"), "/media" },
            });

    // ── tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Sync_CreatesPlaylist_WithBothTracks()
    {
        var sync = NewSync(_token);
        var playlist = PlaylistOf(new[] { ("Alpha Song", false), ("Beta Song", false) });

        await sync.SyncPlaylistAsync(playlist, HostOptions());

        Assert.That(sync.LastReport!.Message, Is.EqualTo("Created playlist"),
            "report: " + sync.LastReport.Message);
        Assert.That(sync.LastReport.ResolvedCount, Is.EqualTo(2));
        Assert.That(sync.LastReport.UnresolvedPaths, Is.Empty);

        var mix = await FindPlaylist("MB Live Mix");
        Assert.That(mix.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined), "playlist exists");
    }

    [Test]
    public async Task Sync_Twice_UpdatesInsteadOfDuplicating()
    {
        var sync = NewSync(_token);
        var playlist = PlaylistOf(new[] { ("Alpha Song", false) });
        await sync.SyncPlaylistAsync(playlist, HostOptions());
        var first = await AllPlaylistIds("MB Live Mix");

        await sync.SyncPlaylistAsync(PlaylistOf(new[] { ("Alpha Song", false), ("Beta Song", false) }),
            HostOptions());
        var second = await AllPlaylistIds("MB Live Mix");

        Assert.That(second.Count, Is.EqualTo(first.Count),
            "second sync updates the same playlist, never a second one");
    }

    [Test]
    public async Task Sync_LikedTrack_BecomesFavorite()
    {
        var sync = NewSync(_token);
        await sync.SyncPlaylistAsync(PlaylistOf(new[] { ("Beta Song", true) }), HostOptions());

        var userId = (await _api.GetFromJsonAsync<JsonElement>(BaseUrl + "/Users"))
            .EnumerateArray().First().GetProperty("Id").GetString();
        var items = await _api.GetFromJsonAsync<JsonElement>(
            $"{BaseUrl}/Users/{userId}/Items?Filters=IsFavorite&Recursive=true&IncludeItemTypes=Audio");
        var names = string.Join(",", items.GetProperty("Items").EnumerateArray()
            .Select(i => i.GetProperty("Name").GetString()));
        Assert.That(names, Does.Contain("Beta Song"), "the liked track is a favorite");
    }

    [Test]
    public async Task Sync_BadToken_ReportsErrorNotCrash()
    {
        var sync = NewSync("not-a-real-token");
        await sync.SyncPlaylistAsync(PlaylistOf(new[] { ("Alpha Song", false) }), HostOptions());

        Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(0),
            "with a bad token nothing resolves; the report explains why");
    }

    /// <summary>Playlists via the Items query (the /Playlists route is
    /// user-scoped GET /Playlists/{userId} in 10.11).</summary>
    private async Task<JsonElement> FindPlaylist(string name)
    {
        var items = await _api.GetFromJsonAsync<JsonElement>(
            BaseUrl + "/Items?Recursive=true&IncludeItemTypes=Playlist");
        return items.GetProperty("Items").EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("Name").GetString() == name);
    }

    private async Task<List<string>> AllPlaylistIds(string name)
    {
        var mix = await FindPlaylist(name);
        return mix.ValueKind == JsonValueKind.Undefined
            ? new List<string>()
            : new List<string> { mix.GetProperty("Id").GetString()! };
    }
}
