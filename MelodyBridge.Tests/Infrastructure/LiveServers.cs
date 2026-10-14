using System.Net.Http.Json;
using System.Text.Json;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Real media-server containers for the live suites, shared by the sync
/// fixtures and the release end-to-end test: start the container, wait for
/// the server, bootstrap it (Jellyfin wizard and library, Navidrome admin,
/// Plex identity) and query it back. Every method throws on real failures;
/// callers gate on DockerImageReady first, so a host without Docker skips
/// instead of failing.
/// </summary>
public static class LiveServers
{
    /// <summary>The checkout root: FindRepoRoot in the old fixtures.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.Name.Equals("MelodyBridge.Tests", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;
        return dir!.Parent!.FullName;
    }

    /// <summary>Creates (if needed) and returns a scratch dir under .mb-live.</summary>
    public static string ScratchDir(string name)
    {
        var dir = Path.Combine(RepoRoot(), ".mb-live", name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// A real Jellyfin container, bootstrapped headless: startup wizard, admin
/// login, a music library over /media and a scan that surfaced the expected
/// files. The API client carries the login token.
/// </summary>
public sealed class JellyfinLiveServer : IDisposable
{
    public const string BaseUrl = "http://127.0.0.1:8096";
    public const string AdminUser = "mbadmin";
    public const string AdminPw = "mb-admin-pw-123";
    private const string Container = "mb-live-test-jellyfin";

    public required string Token { get; init; }
    public required HttpClient Api { get; init; }

    /// <summary>
    /// Starts a fresh container with the given host folder mounted at
    /// /media, waits for the loaded server, runs the wizard, logs in and
    /// scans the library until at least expectedTracks audio items exist.
    /// </summary>
    public static async Task<JellyfinLiveServer> StartAsync(string musicHostDir, int expectedTracks)
    {
        var config = Path.Combine(LiveServers.ScratchDir("jellyfin"), "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);

        DockerOrIgnore($"rm -f {Container}");
        LiveFixtureHelpers.Docker($"run -d --name {Container} -p 127.0.0.1:8096:8096 " +
            $"-v {musicHostDir}:/media:ro -v {config}:/config jellyfin/jellyfin:latest", 120);

        await WaitUntilReadyAsync();
        await CompleteStartupWizardAsync();
        var token = await AuthenticateAsync();
        var api = new HttpClient();
        api.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
        await AddMusicLibraryAsync(api);
        await WaitUntilScannedAsync(api, expectedTracks);
        return new JellyfinLiveServer { Token = token, Api = api };
    }

    public void Dispose()
    {
        Api.Dispose();
        try { LiveFixtureHelpers.Docker($"rm -f {Container}", 30); } catch { /* best effort */ }
    }

    /// <summary>Best-effort container state for a failed wait: what the
    /// container sees under /media plus the log tail.</summary>
    public static string ContainerLog()
    {
        try
        {
            var media = LiveFixtureHelpers.Docker($"exec {Container} ls /media").Trim();
            var log = LiveFixtureHelpers.Docker($"logs --tail 30 {Container}");
            return $"media=[{media}] log={log}";
        }
        catch
        {
            return "(log unavailable)";
        }
    }

    /// <summary>
    /// Waits for the server core, not just the web host: /System/Info/Public
    /// answers while the database is still loading, and the startup API
    /// answers 503 until that finishes.
    /// </summary>
    private static async Task WaitUntilReadyAsync()
    {
        using var http = new HttpClient();
        string? last = null;
        for (var i = 0; i < 240; i++)
        {
            try
            {
                using var resp = await http.GetAsync(BaseUrl + "/Startup/Configuration");
                if ((int)resp.StatusCode != 503) return;
                last = "HTTP 503 (server still loading)";
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }
            await Task.Delay(2000);
        }
        Assert.Fail($"jellyfin did not finish loading within 8 minutes (last: {last}). Container log: " + ContainerLog());
    }

    /// <summary>Wizard order matters: configuration, initialize the default
    /// user (GET), rename + password it (POST), complete.</summary>
    private static async Task CompleteStartupWizardAsync()
    {
        using var http = new HttpClient();
        await PostAsync(http, "/Startup/Configuration", """{"UICulture":"en-US"}""");
        await http.GetStringAsync(BaseUrl + "/Startup/User");
        await PostAsync(http, "/Startup/User", $$"""{"Name":"{{AdminUser}}","Password":"{{AdminPw}}"}""");
        await PostAsync(http, "/Startup/Complete", "{}");
    }

    private static async Task PostAsync(HttpClient http, string path, string json)
    {
        using var resp = await http.PostAsync(BaseUrl + path,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"POST {path} -> {(int)resp.StatusCode}");
    }

    /// <summary>Logs in with the modern Authorization header: recent Jellyfin
    /// ignores the legacy X-Emby-Authorization header on this endpoint and
    /// answers 400 because no client name arrives.</summary>
    private static async Task<string> AuthenticateAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            "MediaBrowser Client=\"MelodyBridge\", Device=\"mb-test\", DeviceId=\"mb-live\", Version=\"1.0\"");
        using var resp = await http.PostAsync(BaseUrl + "/Users/AuthenticateByName",
            new StringContent($$"""{"Username":"{{AdminUser}}","Pw":"{{AdminPw}}"}""",
                System.Text.Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(resp.IsSuccessStatusCode, Is.True, $"admin login -> {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("AccessToken").GetString()!;
    }

    private static async Task AddMusicLibraryAsync(HttpClient api)
    {
        using var resp = await api.PostAsync(
            BaseUrl + "/Library/VirtualFolders?name=Music&collectionType=music&paths=/media&refreshLibrary=true",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.That(resp.IsSuccessStatusCode, Is.True,
            $"music library created -> {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Waits until the library scan surfaced at least the expected number of
    /// audio items. A Jellyfin first boot on a slow disk can take minutes,
    /// and the scan queued by the library creation can run before the mount
    /// settles; kick a refresh while waiting so the wait never sits on a
    /// scan that found nothing.
    /// </summary>
    public static async Task WaitUntilScannedAsync(HttpClient api, int expected)
    {
        var last = 0;
        for (var i = 0; i < 120; i++)
        {
            var items = await api.GetFromJsonAsync<JsonElement>(
                BaseUrl + "/Items?Recursive=true&IncludeItemTypes=Audio");
            last = items.GetProperty("TotalRecordCount").GetInt32();
            if (last >= expected) return;

            if (i % 10 == 9)
            {
                using var refresh = await api.PostAsync(BaseUrl + "/Library/Refresh", null);
            }
            await Task.Delay(2000);
        }
        Assert.Fail($"library scan did not surface the expected {expected} track(s) (last count: {last}). Container log: " + ContainerLog());
    }

    /// <summary>The scanner surfaces an item in /Items before its metadata is
    /// searchable; the sync's reliable lookup against a login token is the
    /// title/artist search, so wait until the titles answer.</summary>
    public static async Task WaitUntilResolvableAsync(HttpClient api, string title)
    {
        var last = 0;
        for (var i = 0; i < 120; i++)
        {
            var list = await api.GetFromJsonAsync<JsonElement>(
                BaseUrl + "/Items?Recursive=true&IncludeItemTypes=Audio&SearchTerm=" + Uri.EscapeDataString(title));
            last = list.GetProperty("TotalRecordCount").GetInt32();
            if (last > 0) return;
            await Task.Delay(2000);
        }
        Assert.Fail($"{title} never became searchable (last count: {last}). Container log: " + ContainerLog());
    }

    /// <summary>Every playlist with this name (Items query: the /Playlists
    /// route is user-scoped in 10.11).</summary>
    public async Task<List<JsonElement>> PlaylistsNamedAsync(string name)
    {
        var items = await Api.GetFromJsonAsync<JsonElement>(
            BaseUrl + "/Items?Recursive=true&IncludeItemTypes=Playlist");
        return items.GetProperty("Items").EnumerateArray()
            .Where(p => p.GetProperty("Name").GetString() == name)
            .ToList();
    }

    /// <summary>The item names inside one playlist, read back from Jellyfin.</summary>
    public async Task<List<string>> PlaylistItemNamesAsync(string playlistId)
    {
        var items = await Api.GetFromJsonAsync<JsonElement>(
            BaseUrl + "/Playlists/" + playlistId + "/Items");
        return items.GetProperty("Items").EnumerateArray()
            .Select(i => i.GetProperty("Name").GetString() ?? "")
            .ToList();
    }

    /// <summary>
    /// Fetches the first bytes of an item through Jellyfin's own stream
    /// endpoint: proof the pushed playlist entry points at real, servable
    /// audio, not just a metadata row.
    /// </summary>
    public async Task<byte[]> StreamFirstBytesAsync(string itemId, int count = 2048)
    {
        using var resp = await Api.GetAsync(
            BaseUrl + "/Audio/" + itemId + "/stream?static=true", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(resp.IsSuccessStatusCode, Is.True,
            $"stream {itemId} -> {(int)resp.StatusCode}");
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var buffer = new byte[count];
        var read = await stream.ReadAsync(buffer);
        return buffer[..read];
    }

    private static void DockerOrIgnore(string args)
    {
        try { LiveFixtureHelpers.Docker(args); }
        catch (InvalidOperationException ex) { Assert.Ignore(ex.Message); }
    }
}

/// <summary>
/// A real Navidrome container with the admin password bootstrap env and a
/// scanned music folder. Subsonic requests use the same salted-md5 auth
/// the app uses.
/// </summary>
public sealed class NavidromeLiveServer : IDisposable
{
    public const string BaseUrl = "http://127.0.0.1:4533";
    public const string Username = "admin";
    public const string Password = "mb-admin-pw-123";
    private const string Container = "mb-live-test-navidrome";

    public static async Task<NavidromeLiveServer> StartAsync(string musicHostDir, int expectedTracks)
    {
        var data = Path.Combine(LiveServers.ScratchDir("navidrome"), "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);

        DockerOrIgnore($"rm -f {Container}");
        // ND_DEVAUTOCREATEADMINPASSWORD creates the admin user on first run.
        LiveFixtureHelpers.Docker($"run -d --name {Container} -p 127.0.0.1:4533:4533 " +
            $"-e ND_PORT=4533 -e ND_DEVAUTOCREATEADMINPASSWORD={Password} -e ND_SCANNER_SCANONSTARTUP=true " +
            $"-v {musicHostDir}:/music:ro -v {data}:/data deluan/navidrome:latest", 120);

        var server = new NavidromeLiveServer();
        await server.WaitUntilScannedAsync(expectedTracks);
        return server;
    }

    public void Dispose()
    {
        try { LiveFixtureHelpers.Docker($"rm -f {Container}", 30); } catch { /* best effort */ }
    }

    /// <summary>Best-effort container log tail for a failed wait.</summary>
    public static string ContainerLog()
    {
        try { return LiveFixtureHelpers.Docker($"logs --tail 30 {Container}"); }
        catch { return "(log unavailable)"; }
    }

    /// <summary>Subsonic request with the salted-md5 auth this app uses.</summary>
    public string SubsonicUrl(string endpoint, string extra = "")
    {
        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var token = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(Password + salt)))
            .ToLowerInvariant();
        return $"{BaseUrl}/rest/{endpoint}?u={Username}&t={token}&s={salt}&v=1.16.1&c=melodybridge&f=json{extra}";
    }

    public async Task<JsonElement> GetSubsonicAsync(string endpoint, string extra = "")
    {
        using var http = new HttpClient();
        var body = await http.GetFromJsonAsync<JsonElement>(SubsonicUrl(endpoint, extra));
        return body.GetProperty("subsonic-response");
    }

    private async Task WaitUntilScannedAsync(int expected)
    {
        string? last = null;
        for (var i = 0; i < 45; i++)
        {
            try
            {
                var status = (await GetSubsonicAsync("getScanStatus")).GetProperty("scanStatus");
                if (!status.GetProperty("scanning").GetBoolean()
                    && status.GetProperty("count").GetInt64() >= expected) return;
                last = status.GetRawText();
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }
            await Task.Delay(2000);
        }
        Assert.Fail($"navidrome did not finish scanning {expected} test track(s) (last: {last}). Container log: " + ContainerLog());
    }

    private static void DockerOrIgnore(string args)
    {
        try { LiveFixtureHelpers.Docker(args); }
        catch (InvalidOperationException ex) { Assert.Ignore(ex.Message); }
    }
}

/// <summary>
/// A real Plex container. An unclaimed headless Plex answers /identity
/// without a token, which is the reachability probe the wizard and the
/// sync use; full playlist sync needs a claimed server and PLEX_TOKEN.
/// </summary>
public sealed class PlexLiveServer : IDisposable
{
    public const string BaseUrl = "http://127.0.0.1:32400";
    private const string Container = "mb-live-test-plex";

    public required HttpClient Http { get; init; }

    public static async Task<PlexLiveServer> StartAsync()
    {
        var config = Path.Combine(LiveServers.ScratchDir("plex"), "config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);

        DockerOrIgnore($"rm -f {Container}");
        LiveFixtureHelpers.Docker($"run -d --name {Container} -p 127.0.0.1:32400:32400 " +
            $"-v {config}:/config plexinc/pms-docker:latest", 120);

        await WaitUntilIdentityAsync();
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        http.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "melodybridge");
        return new PlexLiveServer { Http = http };
    }

    public void Dispose()
    {
        Http.Dispose();
        try { LiveFixtureHelpers.Docker($"rm -f {Container}", 30); } catch { /* best effort */ }
    }

    /// <summary>Best-effort container log tail for a failed wait.</summary>
    public static string ContainerLog()
    {
        try { return LiveFixtureHelpers.Docker($"logs --tail 30 {Container}"); }
        catch { return "(log unavailable)"; }
    }

    /// <summary>
    /// Waits for /identity. Plex runs database migrations and a vacuum on
    /// its first boot; that took over 5 minutes on a slow Windows disk while
    /// CI needs under 2. Budget 12 minutes so a slow disk cannot fail the
    /// suite.
    /// </summary>
    private static async Task WaitUntilIdentityAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        string? last = null;
        for (var i = 0; i < 360; i++)
        {
            try
            {
                using var resp = await http.GetAsync(BaseUrl + "/identity");
                if (resp.IsSuccessStatusCode) return;
                last = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }
            await Task.Delay(2000);
        }
        Assert.Fail($"plex /identity did not answer within 12 minutes (last: {last}). Container log: " + ContainerLog());
    }

    private static void DockerOrIgnore(string args)
    {
        try { LiveFixtureHelpers.Docker(args); }
        catch (InvalidOperationException ex) { Assert.Ignore(ex.Message); }
    }
}
