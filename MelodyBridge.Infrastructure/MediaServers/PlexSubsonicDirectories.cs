using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Plex reachability + user list for the wizard's Test connection button.
/// /identity proves the server is reachable (no token); GET / proves the
/// token works. Plex has no user list: the account behind the token is
/// the only user: so GetUsersAsync returns the server's friendly name.
/// </summary>
public class PlexDirectory : IMediaServerDirectory
{
    public string Kind => "Plex";

    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = DefaultHttp;

    /// <summary>DI path: shares the static default client.</summary>
    public PlexDirectory() { }

    /// <summary>Test seam: injects a scripted client (tests only).</summary>
    internal PlexDirectory(HttpClient http) => _http = http;

    public async Task<bool> TestConnectionAsync(
        MediaServerConnection connection, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                PlexSync.NormalizeBaseUrl(connection.BaseUrl).TrimEnd('/') + "/");
            request.Headers.Add("X-Plex-Token", connection.ApiKey);
            request.Headers.Add("X-Plex-Client-Identifier", "melodybridge");
            request.Headers.Add("Accept", "application/json");
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<List<MediaServerUserOption>> GetUsersAsync(
        MediaServerConnection connection, CancellationToken ct = default)
    {
        // Plex's token-holder is the only user; there is no user list API.
        return Task.FromResult(new List<MediaServerUserOption>());
    }

    public async Task<MediaServerIdentity?> IdentifyAsync(
        MediaServerConnection connection, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                PlexSync.NormalizeBaseUrl(connection.BaseUrl).TrimEnd('/') + "/");
            request.Headers.Add("X-Plex-Token", connection.ApiKey);
            request.Headers.Add("X-Plex-Client-Identifier", "melodybridge");
            request.Headers.Add("Accept", "application/json");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("MediaContainer", out var container))
                return null;
            var server = container.TryGetProperty("friendlyName", out var f) ? f.GetString() : null;
            var account = container.TryGetProperty("myPlexUsername", out var a) ? a.GetString() : null;
            return new MediaServerIdentity(server, account);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Subsonic reachability for the wizard's Test connection button: any
/// server that speaks the Subsonic/OpenSubsonic API (Navidrome, gonic,
/// Airsonic-Advanced, Ampache, Funkwhale, Nextcloud Music). Auth is
/// username + salted-md5 token: the ApiKey parameter carries the password.
/// </summary>
public class SubsonicDirectory : IMediaServerDirectory
{
    public string Kind => "Subsonic";

    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = DefaultHttp;

    /// <summary>DI path: shares the static default client.</summary>
    public SubsonicDirectory() { }

    /// <summary>Test seam: injects a scripted client (tests only).</summary>
    internal SubsonicDirectory(HttpClient http) => _http = http;

    public async Task<bool> TestConnectionAsync(
        MediaServerConnection connection, CancellationToken ct = default)
    {
        // ping is the cheapest authenticated call: the connection carries
        // the real username (UserId) and password (ApiKey), so this verifies
        // the credentials a sync would actually use.
        var username = string.IsNullOrWhiteSpace(connection.UserId) ? "admin" : connection.UserId!;
        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var token = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(connection.ApiKey + salt))).ToLowerInvariant();
        var url = $"{SubsonicSync.NormalizeBaseUrl(connection.BaseUrl).TrimEnd('/')}/rest/ping" +
                  $"?u={Uri.EscapeDataString(username)}&t={token}&s={salt}&v=1.16.1&c=melodybridge&f=json";
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString() == "ok";
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<List<MediaServerUserOption>> GetUsersAsync(
        MediaServerConnection connection, CancellationToken ct = default)
    {
        // Subsonic users are their own credentials; no directory to list.
        return Task.FromResult(new List<MediaServerUserOption>());
    }

    public Task<MediaServerIdentity?> IdentifyAsync(
        MediaServerConnection connection, CancellationToken ct = default)
    {
        // TestConnectionAsync verified the salted-token login, so the
        // configured username is the account that just answered.
        var username = string.IsNullOrWhiteSpace(connection.UserId) ? null : connection.UserId;
        return Task.FromResult<MediaServerIdentity?>(
            new MediaServerIdentity(ServerName: null, AccountName: username));
    }
}
