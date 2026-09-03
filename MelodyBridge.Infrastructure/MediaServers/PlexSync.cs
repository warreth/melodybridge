using System.Net.Http.Json;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Pushes a playlist into a Plex Media Server through its HTTP API. Auth is
/// an X-Plex-Token (the server's claimed token); tracks are resolved by
/// matching the Plex library's Part.file paths against the local paths
/// (after remap); playlists are created with the server:// uri form that
/// python-plexapi uses. Liked tracks get userRating=10 (Plex's "thumbs up").
/// </summary>
public class PlexSync : IMediaServerSync
{
    private const string ClientIdentifier = "melodybridge";

    private readonly HttpClient _http;
    private readonly ILogger<PlexSync> _logger;
    private readonly IPlexSettings _settings;
    public string Name => "Plex";

    private MediaServerSyncReport? _lastReport;
    public MediaServerSyncReport? LastReport => _lastReport;

    public PlexSync(HttpClient http, ILogger<PlexSync> logger, IPlexSettings settings)
    {
        _http = http;
        _logger = logger;
        _settings = settings;
    }

    private async Task<(string BaseUrl, string ApiKey)> ConnectionAsync(
        PlaylistOutputOptions options, CancellationToken ct)
    {
        if (options.MediaServerConnection is { } conn)
            return (conn.BaseUrl, conn.ApiKey);
        return (await _settings.GetBaseUrlAsync(ct), await _settings.GetApiKeyAsync(ct));
    }

    /// <summary>One request with this call's connection applied.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        string baseUrl, string apiKey, string relativeUrl, HttpMethod? method = null,
        HttpContent? content = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            method ?? HttpMethod.Get, NormalizeBaseUrl(baseUrl).TrimEnd('/') + "/" + relativeUrl.TrimStart('/'));
        request.Headers.Add("X-Plex-Token", apiKey);
        request.Headers.Add("X-Plex-Client-Identifier", ClientIdentifier);
        request.Headers.Add("Accept", "application/json");
        if (content is not null)
            request.Content = content;
        return await _http.SendAsync(request, ct);
    }

    /// <summary>Parses the MediaContainer JSON envelope Plex wraps everything in.</summary>
    private static JsonElement? ContainerOf(JsonDocument doc)
        => doc.RootElement.TryGetProperty("MediaContainer", out var c) ? c : null;

    public async Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        if (playlist?.Name == null) throw new ArgumentException("Playlist needs a name");

        var (baseUrl, apiKey) = await ConnectionAsync(options, ct);

        // Server identity: needed for the playlist uri form.
        string? machineId = null;
        try
        {
            using var resp = await SendAsync(baseUrl, apiKey, "/identity", ct: ct);
            if (resp.IsSuccessStatusCode)
                machineId = (await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct))
                    .RootElement.GetProperty("MediaContainer").GetProperty("machineIdentifier").GetString();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Plex /identity failed"); }
        if (machineId is null)
        {
            _lastReport = new MediaServerSyncReport(0, Array.Empty<string>(), null,
                "Could not reach the Plex server (missing machineIdentifier). Check the URL and token.");
            return;
        }

        var sectionId = await FindMusicSectionAsync(baseUrl, apiKey, ct);
        if (sectionId is null)
        {
            _lastReport = new MediaServerSyncReport(0, Array.Empty<string>(), null,
                "No music (artist-type) library section found on the Plex server.");
            return;
        }

        var ratingKeys = new List<string>();
        var likedKeys = new List<string>();
        var unresolved = new List<string>();

        if (playlist.Tracks != null)
        {
            foreach (var track in playlist.Tracks)
            {
                var lookupPath = JellyfinSync.RemapPath(track.CurrentTrackLocation?.Path, options);
                if (lookupPath is null)
                {
                    _logger.LogDebug("No path available for track {title}, skipping lookup", track.Title);
                    continue;
                }

                var key = await ResolveRatingKeyAsync(baseUrl, apiKey, sectionId, lookupPath, track, ct);
                if (key is null)
                {
                    _logger.LogInformation("Could not resolve item for path {path}", lookupPath);
                    unresolved.Add(lookupPath);
                    continue;
                }
                ratingKeys.Add(key);
                if (track.IsLiked) likedKeys.Add(key);
            }
        }

        await UpsertPlaylistAsync(baseUrl, apiKey, machineId, playlist.Name, ratingKeys, unresolved, ct);
        await RateAsync(baseUrl, apiKey, likedKeys, ct);
    }

    private async Task<string?> FindMusicSectionAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(baseUrl, apiKey, "/library/sections", ct: ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var container = ContainerOf(doc);
            if (container?.TryGetProperty("Directory", out var dirs) != true) return null;
            foreach (var dir in dirs.EnumerateArray())
            {
                if (dir.GetProperty("type").GetString() == "artist")
                    return dir.GetProperty("key").GetString();
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Plex sections lookup failed"); }
        return null;
    }

    /// <summary>
    /// Resolves one local path to a Plex ratingKey by querying the music
    /// section's tracks and matching the server-side file path.
    /// </summary>
    private async Task<string?> ResolveRatingKeyAsync(
        string baseUrl, string apiKey, string sectionId, string lookupPath, Track track, CancellationToken ct)
    {
        var fileName = Path.GetFileName(lookupPath);
        var title = track.Title;
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(fileName)) return null;

        try
        {
            // Query tracks by title when known; else page through the section.
            var url = string.IsNullOrEmpty(title)
                ? $"/library/sections/{sectionId}/all?type=10"
                : $"/library/sections/{sectionId}/all?type=10&title=={Uri.EscapeDataString(title)}";
            using var resp = await SendAsync(baseUrl, apiKey, url, ct: ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var container = ContainerOf(doc);
            if (container?.TryGetProperty("Metadata", out var metas) != true) return null;

            foreach (var meta in metas.EnumerateArray())
            {
                if (!meta.TryGetProperty("Media", out var media) || media.GetArrayLength() == 0) continue;
                if (!media[0].TryGetProperty("Part", out var parts) || parts.GetArrayLength() == 0) continue;
                var file = parts[0].GetProperty("file").GetString();
                if (string.IsNullOrEmpty(file)) continue;

                // Exact file match first; filename fallback.
                if (string.Equals(file, lookupPath, StringComparison.OrdinalIgnoreCase))
                    return meta.GetProperty("ratingKey").GetString();
                if (Path.GetFileName(file) is { } f && string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase))
                    return meta.GetProperty("ratingKey").GetString();
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Plex track lookup failed for {path}", lookupPath); }
        return null;
    }

    /// <summary>Creates or replaces the named audio playlist with the given ratingKeys.</summary>
    private async Task UpsertPlaylistAsync(string baseUrl, string apiKey, string machineId, string name,
        List<string> ratingKeys, List<string> unresolved, CancellationToken ct)
    {
        try
        {
            // Existing playlist with this title?
            string? playlistId = null;
            using (var resp = await SendAsync(baseUrl, apiKey, "/playlists?playlistType=audio", ct: ct))
            {
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (ContainerOf(doc)?.TryGetProperty("Metadata", out var metas) == true)
                    {
                        foreach (var meta in metas.EnumerateArray())
                        {
                            if (meta.TryGetProperty("title", out var t) && t.GetString() == name)
                            {
                                playlistId = meta.GetProperty("ratingKey").GetString();
                                break;
                            }
                        }
                    }
                }
            }

            if (playlistId is not null && ratingKeys.Count > 0)
            {
                // Replace the contents: clear, then re-add.
                using var clear = await SendAsync(baseUrl, apiKey, $"/playlists/{playlistId}/items",
                    HttpMethod.Delete, ct: ct);
                using var add = await SendAsync(baseUrl, apiKey,
                    $"/playlists/{playlistId}/items?uri={Uri.EscapeDataString(ItemsUri(machineId, ratingKeys))}",
                    HttpMethod.Put, ct: ct);
                if (add.IsSuccessStatusCode)
                {
                    _lastReport = new MediaServerSyncReport(ratingKeys.Count, unresolved.ToArray(),
                        playlistId, "Updated existing playlist");
                    return;
                }
                _logger.LogWarning("Plex playlist update failed with {status}; recreating", add.StatusCode);
                using var del = await SendAsync(baseUrl, apiKey, $"/playlists/{playlistId}", HttpMethod.Delete, ct: ct);
            }
            else if (playlistId is not null)
            {
                using var del = await SendAsync(baseUrl, apiKey, $"/playlists/{playlistId}", HttpMethod.Delete, ct: ct);
            }

            if (ratingKeys.Count == 0)
            {
                _lastReport = new MediaServerSyncReport(0, unresolved.ToArray(), null,
                    "No resolved tracks; playlist deleted or not created");
                return;
            }

            using var create = await SendAsync(baseUrl, apiKey,
                $"/playlists?uri={Uri.EscapeDataString(ItemsUri(machineId, ratingKeys))}&type=audio&title={Uri.EscapeDataString(name)}&smart=0",
                HttpMethod.Post, ct: ct);
            if (create.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var id = ContainerOf(doc)?.TryGetProperty("Metadata", out var metas) == true
                    && metas.GetArrayLength() > 0
                    ? metas[0].GetProperty("ratingKey").GetString()
                    : null;
                _lastReport = new MediaServerSyncReport(ratingKeys.Count, unresolved.ToArray(),
                    id, "Created playlist");
            }
            else
            {
                _logger.LogWarning("Plex returned {status} when creating playlist {name}", create.StatusCode, name);
                _lastReport = new MediaServerSyncReport(ratingKeys.Count, unresolved.ToArray(),
                    null, $"Create failed with {create.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Plex API to upsert playlist");
            _lastReport = new MediaServerSyncReport(ratingKeys.Count, unresolved.ToArray(),
                null, $"Error: {ex.Message}");
        }
    }

    /// <summary>python-plexapi's uri form for a playlist of comma-joined ratingKeys.</summary>
    private static string ItemsUri(string machineId, List<string> ratingKeys)
        => $"server://{machineId}/com.plexapp.plugins.library/library/metadata/{string.Join(",", ratingKeys)}";

    /// <summary>Marks liked tracks with userRating=10 (Plex's top rating).</summary>
    private async Task RateAsync(string baseUrl, string apiKey, List<string> likedKeys, CancellationToken ct)
    {
        var rated = 0;
        foreach (var key in likedKeys)
        {
            try
            {
                using var resp = await SendAsync(baseUrl, apiKey,
                    $"/:/rate?key={Uri.EscapeDataString(key)}&identifier=com.plexapp.plugins.library&rating=10",
                    HttpMethod.Put, ct: ct);
                if (resp.IsSuccessStatusCode) rated++;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Plex rating failed for {key}", key); }
        }
        if (likedKeys.Count > 0)
            _logger.LogInformation("Rated {Rated}/{Total} liked songs in Plex", rated, likedKeys.Count);
    }

    /// <summary>Plex URLs need a scheme; default to plain http.</summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }
}
