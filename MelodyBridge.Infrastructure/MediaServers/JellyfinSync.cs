using MelodyBridge.Core;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.IO;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Pushes a playlist into a Jellyfin server through its REST API. Connection
/// values are resolved per call (job override first, then the global
/// settings) and travel on each request, so concurrent syncs never race on
/// a shared HttpClient's BaseAddress or default headers.
/// </summary>
public class JellyfinSync : IMediaServerSync
{
    private readonly HttpClient _http;
    private readonly ILogger<JellyfinSync> _logger;
    private readonly IJellyfinSettings _settings;
    public string Name => "Jellyfin";

    private MediaServerSyncReport? _lastReport;
    public MediaServerSyncReport? LastReport => _lastReport;

    public JellyfinSync(HttpClient http, ILogger<JellyfinSync> logger,
        IJellyfinSettings settings)
    {
        _http = http;
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Connection values for this call: the per-job override (sync-job
    /// wizard) wins; otherwise the global settings are re-read so
    /// Settings-page changes apply without a restart.
    /// </summary>
    private async Task<(string BaseUrl, string ApiKey)> ConnectionAsync(
        PlaylistOutputOptions options, CancellationToken ct)
    {
        if (options.MediaServerConnection is { } conn)
            return (conn.BaseUrl, conn.ApiKey);
        return (await _settings.GetBaseUrlAsync(ct), await _settings.GetApiKeyAsync(ct));
    }

    /// <summary>Sends one request with this call's connection applied.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        string baseUrl, string apiKey, string relativeUrl, HttpMethod? method = null,
        HttpContent? content = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            method ?? HttpMethod.Get, NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/" + relativeUrl.TrimStart('/'));
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Add("X-Emby-Token", apiKey);
        if (content is not null)
            request.Content = content;
        return await _http.SendAsync(request, ct);
    }

    public async Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        if (playlist?.Name == null) throw new ArgumentException("Playlist needs a name");

        var (baseUrl, apiKey) = await ConnectionAsync(options, ct);

        var itemIds = new List<string>();
        var likedItemIds = new List<string>();
        var unresolved = new List<string>();

        if (playlist.Tracks != null)
        {
            foreach (var track in playlist.Tracks)
            {
                var lookupPath = RemapPath(track.CurrentTrackLocation?.Path, options);
                if (lookupPath is null)
                {
                    _logger.LogDebug("No path available for track {title}, skipping lookup", track.Title);
                    continue;
                }

                var itemId = await ResolveItemIdAsync(baseUrl, apiKey, lookupPath, track, ct);
                if (itemId is null)
                {
                    _logger.LogInformation("Could not resolve item for path {path}", lookupPath);
                    unresolved.Add(lookupPath);
                    continue;
                }
                itemIds.Add(itemId);
                if (track.IsLiked) likedItemIds.Add(itemId);
            }
        }

        await UpsertPlaylistAsync(baseUrl, apiKey, playlist.Name, itemIds, unresolved, ct);

        // Liked songs become Jellyfin favorites for the configured user.
        await MarkFavoritesAsync(baseUrl, apiKey, likedItemIds, options.MediaServerConnection?.UserId, ct);
    }

    /// <summary>
    /// Applies the output path and extension remap rules to a track
    /// path (shared by Jellyfin, Plex and Navidrome lookups). An empty
    /// path means "no path known" and stays null.
    /// </summary>
    internal static string? RemapPath(string? path, PlaylistOutputOptions options)
        => string.IsNullOrEmpty(path) ? null : PlaylistPathMapper.Apply(path, options);

    /// <summary>
    /// Resolves one local path to a Jellyfin item id, trying progressively
    /// looser lookups: exact path, full-path search, filename search, then
    /// metadata search. Returns null when the server does not know the track.
    /// </summary>
    private async Task<string?> ResolveItemIdAsync(
        string baseUrl, string apiKey, string lookupPath, Track track, CancellationToken ct)
    {
        // 1) Exact path
        try
        {
            using var resp = await SendAsync(baseUrl, apiKey,
                $"/Items/ByPath?path={System.Net.WebUtility.UrlEncode(lookupPath)}", ct: ct);
            if (resp.IsSuccessStatusCode)
            {
                var item = await resp.Content.ReadFromJsonAsync<JellyfinItem>(cancellationToken: ct);
                if (!string.IsNullOrEmpty(item?.Id)) return item.Id;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "ByPath lookup failed for {path}", lookupPath); }

        // 2) Search by full path, 3) by filename, 4) by title/artist
        var terms = new[]
        {
            lookupPath,
            Path.GetFileName(lookupPath),
            string.Join(' ', new[] { track.Title, track.Artist }.Where(t => !string.IsNullOrEmpty(t)))
        };
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            try
            {
                using var resp = await SendAsync(baseUrl, apiKey,
                    $"/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path&SearchTerm={System.Net.WebUtility.UrlEncode(term)}",
                    ct: ct);
                if (!resp.IsSuccessStatusCode) continue;
                var list = await resp.Content.ReadFromJsonAsync<JellyfinItemsResult>(cancellationToken: ct);
                if (list?.Items is { Length: > 0 }) return list.Items[0].Id;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Search lookup failed for {term}", term); }
        }
        return null;
    }

    /// <summary>Updates the named playlist in place, or creates it.</summary>
    private async Task UpsertPlaylistAsync(string baseUrl, string apiKey, string name,
        List<string> itemIds, List<string> unresolved, CancellationToken ct)
    {
        // Jellyfin binds the member item ids from "Ids" in both its
        // CreatePlaylistDto and UpdatePlaylistDto. The old "PlaylistItems"
        // shape was an unknown property the server ignored, so every
        // create landed as an empty playlist.
        var payload = new { Name = name, Ids = itemIds.ToArray() };
        try
        {
            string? existingId = null;
            using (var existingResp = await SendAsync(baseUrl, apiKey,
                "/Items?Recursive=true&IncludeItemTypes=Playlist", ct: ct))
            {
                if (existingResp.IsSuccessStatusCode)
                {
                    var existing = await existingResp.Content
                        .ReadFromJsonAsync<JellyfinItemsResult>(cancellationToken: ct);
                    existingId = existing?.Items?
                        .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
                }
            }

            if (existingId is not null)
            {
                _logger.LogInformation("Updating existing playlist {name}", name);
                // Jellyfin updates a playlist with POST /Playlists/{id}:
                // there is no PUT route, and no route deletes a playlist
                // whole. A failed update is reported, never duplicated.
                using var update = await SendAsync(baseUrl, apiKey, $"/Playlists/{existingId}",
                    HttpMethod.Post, JsonContent.Create(payload), ct);
                if (update.IsSuccessStatusCode)
                {
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                        existingId, "Updated existing playlist");
                    return;
                }

                if (update.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Jellyfin returned {status} when updating playlist {name}",
                        update.StatusCode, name);
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                        existingId, $"Update failed with {update.StatusCode}");
                    return;
                }
                _logger.LogInformation("Playlist {name} vanished before the update; recreating", name);
            }

            using var resp = await SendAsync(baseUrl, apiKey, "/Playlists",
                HttpMethod.Post, JsonContent.Create(payload), ct);
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var created = await resp.Content.ReadFromJsonAsync<JellyfinPlaylist>(cancellationToken: ct);
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                        created?.Id, "Created playlist");
                }
                catch
                {
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                        null, "Created playlist (no id)");
                }
            }
            else
            {
                _logger.LogWarning("Jellyfin returned {status} when creating playlist {name}",
                    resp.StatusCode, name);
                _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                    null, $"Create failed with {resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Jellyfin API to upsert playlist");
            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                null, $"Error: {ex.Message}");
        }

        if (_lastReport == null)
            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                null, "Completed with warnings");
    }

    /// <summary>
    /// Marks the given items as favorites for the configured Jellyfin user
    /// (POST /Users/{userId}/FavoriteItems/{itemId}, with the older
    /// /Users/{userId}/Items/{itemId}/Favorite route as fallback).
    /// </summary>
    private async Task MarkFavoritesAsync(string baseUrl, string apiKey, List<string> likedItemIds,
        string? overrideUserId, CancellationToken ct)
    {
        if (likedItemIds.Count == 0) return;

        var userId = !string.IsNullOrWhiteSpace(overrideUserId) ? overrideUserId
            : await FindUserIdAsync(baseUrl, apiKey, ct);
        if (userId is null)
        {
            _logger.LogInformation(
                "Skipping {Count} favorites: no Jellyfin user configured", likedItemIds.Count);
            return;
        }

        var marked = 0;
        foreach (var itemId in likedItemIds)
        {
            try
            {
                using var modern = await SendAsync(baseUrl, apiKey,
                    $"/Users/{userId}/FavoriteItems/{itemId}", HttpMethod.Post, ct: ct);
                if (modern.IsSuccessStatusCode) { marked++; continue; }

                // Older Jellyfin: /Users/{userId}/Items/{itemId}/Favorite
                using var legacy = await SendAsync(baseUrl, apiKey,
                    $"/Users/{userId}/Items/{itemId}/Favorite", HttpMethod.Post, ct: ct);
                if (legacy.IsSuccessStatusCode) marked++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Favorite mark failed for {ItemId}", itemId);
            }
        }
        _logger.LogInformation("Marked {Marked}/{Total} liked songs as Jellyfin favorites",
            marked, likedItemIds.Count);
    }

    /// <summary>
    /// The configured user id; when empty, the first non-system Jellyfin
    /// user is used (single-user servers).
    /// </summary>
    private async Task<string?> FindUserIdAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var configured = await _settings.GetUserIdAsync(ct);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        try
        {
            using var response = await SendAsync(baseUrl, apiKey, "/Users", ct: ct);
            if (!response.IsSuccessStatusCode) return null;
            var users = await response.Content
                .ReadFromJsonAsync<JellyfinUserDto[]>(cancellationToken: ct);
            return users?.FirstOrDefault(u =>
                       !string.Equals(u.Name, "system", StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "User lookup failed");
            return null;
        }
    }

    /// <summary>Jellyfin URLs need a scheme; default to plain http.</summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }

    private class JellyfinItem { public string? Id { get; set; } public string? Name { get; set; } }
    private class JellyfinItemsResult { public JellyfinItem[]? Items { get; set; } }
    private class JellyfinPlaylist { public string? Id { get; set; } public string? Name { get; set; } }
}
