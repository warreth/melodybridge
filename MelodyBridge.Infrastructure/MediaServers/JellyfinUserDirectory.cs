using System.Net.Http.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Reads users and reachability of an arbitrary Jellyfin server, using the
/// URL and API key entered in the wizard (not the global settings). The
/// token travels per request, so nothing mutable is shared with the sync
/// client and concurrent tests cannot leak state.
/// </summary>
public class JellyfinUserDirectory : IMediaServerDirectory
{
    public string Kind => "Jellyfin";

    // Own client: per-request headers only, fixed short timeout for the
    // wizard's Test connection button.
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = DefaultHttp;

    public JellyfinUserDirectory()
    {
    }

    /// <summary>Test seam: injects a scripted client (tests only).</summary>
    internal JellyfinUserDirectory(HttpClient http) => _http = http;

    public async Task<List<MediaServerUserOption>> GetUsersAsync(
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var request = NewRequest(baseUrl, apiKey, "Users");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var users = await response.Content
            .ReadFromJsonAsync<JellyfinUserDto[]>(cancellationToken: ct);
        return users?
            .Where(u => !string.IsNullOrEmpty(u.Id))
            .Select(u => new MediaServerUserOption(u.Id!, u.Name))
            .ToList() ?? new List<MediaServerUserOption>();
    }

    public async Task<bool> TestConnectionAsync(
        string baseUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            using var request = NewRequest(baseUrl, apiKey, "System/Info");
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static HttpRequestMessage NewRequest(
        string baseUrl, string apiKey, string relative)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/" + relative);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Add("X-Emby-Token", apiKey);
        return request;
    }

    /// <summary>Jellyfin URLs need a scheme; default to plain http.</summary>
    private static string NormalizeBaseUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }
}
