using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Live Navidrome end-to-end: a real container with the admin password
/// bootstrap env, a scanned music folder, then NavidromeSync resolves the
/// tagged files by title+artist (paths are metadata-built in Navidrome and
/// cannot be matched), creates and re-creates a playlist without
/// duplicates, and stars the liked track. Docker is required; the test
/// ignores itself when the daemon or image is missing.
/// </summary>
[TestFixture]
[Category("Live")]
public class NavidromeSyncLiveTests
{
    private const string BaseUrl = "http://127.0.0.1:4533";
    private const string Username = "admin";
    private const string Password = "mb-admin-pw-123";
    private const string Container = "mb-live-test-navidrome";

    private string _music = null!;
    private string _config = null!;
    private HttpClient _syncClient = null!;

    private sealed class FixedSettings : INavidromeSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default) => Task.FromResult(Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default) => Task.FromResult(Password);
    }

    private sealed class BadPasswordSettings : INavidromeSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default) => Task.FromResult(Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default) => Task.FromResult("not-the-password");
    }

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Assume.That(LiveFixtureHelpers.DockerImageReady("deluan/navidrome:latest"),
            "navidrome image or docker daemon unavailable");

        var root = Path.Combine(FindRepoRoot(), ".mb-live", "navidrome");
        _music = Path.Combine(root, "music");
        _config = Path.Combine(root, "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_music);
        Directory.CreateDirectory(_config);
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(_music, "alpha.mp3"), "Alpha Song", "Alice");
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(_music, "beta.mp3"), "Beta Song", "Bob");

        DockerOrIgnore($"rm -f {Container}");
        // ND_DEVAUTOCREATEADMINPASSWORD creates the admin user on first run.
        LiveFixtureHelpers.Docker($"run -d --name {Container} --network host " +
            $"-e ND_PORT=4533 -e ND_DEVAUTOCREATEADMINPASSWORD={Password} -e ND_SCANNER_SCANONSTARTUP=true " +
            $"-v {_music}:/music:ro -v {_config}:/data deluan/navidrome:latest", 120);

        await WaitUntilScanned();
        _syncClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { LiveFixtureHelpers.Docker($"rm -f {Container}", 30); } catch { /* best effort */ }
        _syncClient?.Dispose();
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

    /// <summary>Subsonic request with the salted-md5 auth this app uses.</summary>
    private static string SubsonicUrl(string endpoint, string extra = "")
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Password + salt))).ToLowerInvariant();
        return $"{BaseUrl}/rest/{endpoint}?u={Username}&t={token}&s={salt}&v=1.16.1&c=melodybridge&f=json{extra}";
    }

    private async Task<JsonElement> GetSubsonic(string endpoint, string extra = "")
    {
        using var http = new HttpClient();
        var body = await http.GetFromJsonAsync<JsonElement>(SubsonicUrl(endpoint, extra));
        return body.GetProperty("subsonic-response");
    }

    private async Task WaitUntilScanned()
    {
        for (var i = 0; i < 45; i++)
        {
            try
            {
                var status = (await GetSubsonic("getScanStatus")).GetProperty("scanStatus");
                if (!status.GetProperty("scanning").GetBoolean()
                    && status.GetProperty("count").GetInt64() >= 2) return;
            }
            catch { /* server not up yet */ }
            await Task.Delay(2000);
        }
        Assert.Fail("navidrome did not finish scanning the two test tracks");
    }

    // ── the sync under test ──────────────────────────────────────────

    private NavidromeSync NewSync()
        => new(_syncClient, NullLogger<NavidromeSync>.Instance, new FixedSettings());

    private static Playlist PlaylistOf(params (string title, string artist, bool liked)[] tracks) => new()
    {
        Name = "MB Live Mix",
        Tracks = tracks.Select(t => new Track
        {
            Title = t.title,
            Artist = t.artist,
            IsLiked = t.liked,
            CurrentTrackLocation = new FileLocation("/music/" +
                (t.title.Contains("Alpha") ? "alpha.mp3" : "beta.mp3")),
        }).ToList(),
    };

    private static PlaylistOutputOptions Options() => new("/tmp/ignored.m3u", false, null);

    // ── tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Sync_CreatesPlaylist_AndSearchFindsBothTracks()
    {
        var sync = NewSync();
        await sync.SyncPlaylistAsync(
            PlaylistOf(("Alpha Song", "Alice", false), ("Beta Song", "Bob", false)), Options());

        Assert.That(sync.LastReport!.Message, Is.EqualTo("Created playlist"));
        Assert.That(sync.LastReport.ResolvedCount, Is.EqualTo(2),
            "both tagged files resolve via title+artist search3");
        Assert.That(sync.LastReport.UnresolvedPaths, Is.Empty);
        Assert.That(sync.LastReport.PlaylistId, Is.Not.Null);

        var playlists = await GetSubsonic("getPlaylists");
        var names = playlists.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString());
        Assert.That(names, Does.Contain("MB Live Mix"));
    }

    [Test]
    public async Task Sync_Twice_UpdatesInsteadOfDuplicating()
    {
        var sync = NewSync();
        await sync.SyncPlaylistAsync(PlaylistOf(("Alpha Song", "Alice", false)), Options());
        await sync.SyncPlaylistAsync(
            PlaylistOf(("Alpha Song", "Alice", false), ("Beta Song", "Bob", false)), Options());

        var playlists = await GetSubsonic("getPlaylists");
        var mixes = playlists.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Where(p => p.GetProperty("name").GetString() == "MB Live Mix").ToList();
        Assert.That(mixes.Count, Is.EqualTo(1),
            "the second sync replaces the track list instead of creating a duplicate");
    }

    [Test]
    public async Task Sync_LikedTrack_BecomesStarred()
    {
        var sync = NewSync();
        await sync.SyncPlaylistAsync(PlaylistOf(("Beta Song", "Bob", true)), Options());

        var starred = await GetSubsonic("getStarred2");
        var names = starred.GetProperty("starred2").GetProperty("song").EnumerateArray()
            .Select(s => s.GetProperty("title").GetString());
        Assert.That(names, Does.Contain("Beta Song"), "the liked track carries a star");
    }

    [Test]
    public async Task Sync_WrongPassword_ReportsErrorNotCrash()
    {
        var sync = new NavidromeSync(_syncClient, NullLogger<NavidromeSync>.Instance,
            new BadPasswordSettings());
        await sync.SyncPlaylistAsync(PlaylistOf(("Alpha Song", "Alice", false)), Options());

        Assert.That(sync.LastReport!.Message, Does.Contain("password").Or.Contain("Wrong"),
            "the subsonic auth failure surfaces in the report: " + sync.LastReport.Message);
    }
}
