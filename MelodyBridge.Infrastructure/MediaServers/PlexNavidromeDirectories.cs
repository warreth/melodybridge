using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Plex reachability + user list for the wizard's Test connection button.
/// /identity proves the server is reachable (no token); GET / proves the
/// token works. Plex has no user list — the account behind the token is
/// the only user — so GetUsersAsync returns the server's friendly name.
/// </summary>
public class PlexDirectory : IMediaServerDirectory
{
    public string Kind => "Plex";

    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = DefaultHttp;

    /// <summary>Test seam: injects a scripted client (tests only).</summary>
    internal PlexDirectory(HttpClient http) => _http = http;

    public async Task<bool> TestConnectionAsync(
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                PlexSync.NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/");
            request.Headers.Add("X-Plex-Token", apiKey);
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
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        // Plex's token-holder is the only user; there is no user list API.
        return Task.FromResult(new List<MediaServerUserOption>());
    }
}

/// <summary>
/// Navidrome reachability + user list. Auth is username + salted-md5
/// token: the ApiKey parameter carries the password here.
/// </summary>
public class NavidromeDirectory : IMediaServerDirectory
{
    public string Kind => "Navidrome";

    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = DefaultHttp;

    /// <summary>Test seam: injects a scripted client (tests only).</summary>
    internal NavidromeDirectory(HttpClient http) => _http = http;

    public async Task<bool> TestConnectionAsync(
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        // ping is the cheapest authenticated call. ApiKey carries the
        // password here; the directory cannot know the username yet, so
        // an empty username plus valid password still proves the server
        // answers (status ok/failed both mean reachable).
        var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var token = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(apiKey + salt))).ToLowerInvariant();
        var url = $"{NavidromeSync.NormalizeBaseUrl(baseUrl).TrimEnd('/')}/rest/ping" +
                  $"?u=melodybridge&t={token}&s={salt}&v=1.16.1&c=melodybridge&f=json";
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
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        // Navidrome users are their own credentials; no directory to list.
        return Task.FromResult(new List<MediaServerUserOption>());
    }
}
