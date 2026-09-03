using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Pushes a playlist into a Navidrome server through its Subsonic API.
/// Auth is username + salted-md5 token (t=md5(password+salt)), so the
/// password never travels in the clear. Tracks are resolved by searching
/// title/artist (search3) and matching the song the way the UI does;
/// playlists are created with repeated songId parameters and, when the
/// name already exists, the existing playlist is updated instead of
/// duplicated. Liked tracks get starred (the Navidrome "favorite" heart).
/// </summary>
public class NavidromeSync : IMediaServerSync
{
    private const string ClientName = "melodybridge";
    private const string ApiVersion = "1.16.1";

    private readonly HttpClient _http;
    private readonly ILogger<NavidromeSync> _logger;
    private readonly INavidromeSettings _settings;
    public string Name => "Navidrome";

    private MediaServerSyncReport? _lastReport;
    public MediaServerSyncReport? LastReport => _lastReport;

    public NavidromeSync(HttpClient http, ILogger<NavidromeSync> logger, INavidromeSettings settings)
    {
        _http = http;
        _logger = logger;
        _settings = settings;
    }

    private async Task<(string BaseUrl, string Username, string Password)> ConnectionAsync(
        PlaylistOutputOptions options, CancellationToken ct)
    {
        if (options.MediaServerConnection is { } conn)
        {
            // Per-job override: UserId carries the Navidrome username.
            return (conn.BaseUrl, conn.UserId ?? "", conn.ApiKey);
        }
        return (await _settings.GetBaseUrlAsync(ct),
            await _settings.GetUsernameAsync(ct),
            await _settings.GetPasswordAsync(ct));
    }

    /// <summary>Builds the /rest URL with Subsonic auth parameters for one call.</summary>
    private string AuthUrl(string baseUrl, string username, string password, string endpoint, string extra = "")
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))).ToLowerInvariant();
        return $"{NormalizeBaseUrl(baseUrl).TrimEnd('/')}/rest/{endpoint}?u={Uri.EscapeDataString(username)}" +
               $"&t={token}&s={salt}&v={ApiVersion}&c={ClientName}&f=json{extra}";
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        // Clone: the element outlives the disposed document.
        var root = doc.RootElement.GetProperty("subsonic-response").Clone();
        if (root.GetProperty("status").GetString() == "failed")
        {
            var err = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                ? m.GetString() : "unknown Subsonic error";
            throw new InvalidOperationException($"Navidrome API error: {err}");
        }
        return root;
    }

    public async Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        if (playlist?.Name == null) throw new ArgumentException("Playlist needs a name");

        var (baseUrl, username, password) = await ConnectionAsync(options, ct);

        var songIds = new List<string>();
        var likedIds = new List<string>();
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

                var id = await ResolveSongIdAsync(baseUrl, username, password, lookupPath, track, ct);
                if (id is null)
                {
                    _logger.LogInformation("Could not resolve item for path {path}", lookupPath);
                    unresolved.Add(lookupPath);
                    continue;
                }
                songIds.Add(id);
                if (track.IsLiked) likedIds.Add(id);
            }
        }

        await UpsertPlaylistAsync(baseUrl, username, password, playlist.Name, songIds, unresolved, ct);
        await StarAsync(baseUrl, username, password, likedIds, ct);
    }

    /// <summary>
    /// Resolves one track to a Navidrome song id via search3 by title
    /// (artist as tie-breaker). Navidrome's path field is metadata-built,
    /// so path matching is unreliable; title+artist is what the server
    /// itself indexes.
    /// </summary>
    private async Task<string?> ResolveSongIdAsync(
        string baseUrl, string username, string password, string lookupPath, Track track, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(track.Title)) return null;
        try
        {
            var query = string.IsNullOrEmpty(track.Artist)
                ? track.Title
                : $"{track.Artist} {track.Title}";
            var url = AuthUrl(baseUrl, username, password, "search3",
                $"&query={Uri.EscapeDataString(query)}&artistCount=0&albumCount=0&songCount=20");
            var root = await GetJsonAsync(url, ct);
            if (!root.TryGetProperty("searchResult3", out var result)
                || !result.TryGetProperty("song", out var songs))
                return null;

            string? best = null;
            foreach (var song in songs.EnumerateArray())
            {
                var title = song.GetProperty("title").GetString();
                if (!string.Equals(title, track.Title, StringComparison.OrdinalIgnoreCase)) continue;
                var artist = song.TryGetProperty("artist", out var a) ? a.GetString() : null;
                if (!string.IsNullOrEmpty(track.Artist)
                    && !string.Equals(artist, track.Artist, StringComparison.OrdinalIgnoreCase)) continue;
                best = song.GetProperty("id").GetString();
                break;
            }
            return best;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Navidrome search failed for {title}", track.Title); }
        return null;
    }

    /// <summary>Creates the named playlist, or replaces an existing one's tracks.</summary>
    private async Task UpsertPlaylistAsync(string baseUrl, string username, string password, string name,
        List<string> songIds, List<string> unresolved, CancellationToken ct)
    {
        try
        {
            // Subsonic createPlaylist with an existing name duplicates it; find ours first.
            string? existingId = null;
            var listsUrl = AuthUrl(baseUrl, username, password, "getPlaylists");
            var listsRoot = await GetJsonAsync(listsUrl, ct);
            if (listsRoot.TryGetProperty("playlists", out var lists)
                && lists.TryGetProperty("playlist", out var items))
            {
                foreach (var row in items.EnumerateArray())
                {
                    if (row.GetProperty("name").GetString() == name)
                    {
                        existingId = row.GetProperty("id").GetString();
                        break;
                    }
                }
            }

            if (existingId is not null)
            {
                // createPlaylist with playlistId replaces the whole list (upsert-by-id).
                var songParams = string.Concat(songIds.Select(id => $"&songId={Uri.EscapeDataString(id)}"));
                var updateUrl = AuthUrl(baseUrl, username, password, "createPlaylist",
                    $"&playlistId={Uri.EscapeDataString(existingId)}{songParams}");
                await GetJsonAsync(updateUrl, ct);
                _lastReport = new MediaServerSyncReport(songIds.Count, unresolved.ToArray(),
                    existingId, "Updated existing playlist");
                return;
            }

            if (songIds.Count == 0)
            {
                _lastReport = new MediaServerSyncReport(0, unresolved.ToArray(), null,
                    "No resolved tracks; playlist not created");
                return;
            }

            var createParams = string.Concat(songIds.Select(id => $"&songId={Uri.EscapeDataString(id)}"));
            var createUrl = AuthUrl(baseUrl, username, password, "createPlaylist",
                $"&name={Uri.EscapeDataString(name)}{createParams}");
            var root = await GetJsonAsync(createUrl, ct);
            var id = root.TryGetProperty("playlist", out var pl) && pl.TryGetProperty("id", out var pid)
                ? pid.GetString() : null;
            _lastReport = new MediaServerSyncReport(songIds.Count, unresolved.ToArray(),
                id, "Created playlist");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Navidrome API to upsert playlist");
            _lastReport = new MediaServerSyncReport(songIds.Count, unresolved.ToArray(),
                null, $"Error: {ex.Message}");
        }
    }

    /// <summary>Stars liked songs (Navidrome's favorite heart).</summary>
    private async Task StarAsync(string baseUrl, string username, string password, List<string> likedIds, CancellationToken ct)
    {
        var starred = 0;
        foreach (var id in likedIds)
        {
            try
            {
                await GetJsonAsync(AuthUrl(baseUrl, username, password, "star", $"&id={Uri.EscapeDataString(id)}"), ct);
                starred++;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Navidrome star failed for {id}", id); }
        }
        if (likedIds.Count > 0)
            _logger.LogInformation("Starred {Starred}/{Total} liked songs in Navidrome", starred, likedIds.Count);
    }

    /// <summary>Navidrome URLs need a scheme; default to plain http.</summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        return url.Contains("://") ? url : "http://" + url;
    }
}
