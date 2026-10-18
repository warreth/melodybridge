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
            request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
        if (content is not null)
            request.Content = content;
        return await _http.SendAsync(request, ct);
    }

    public async Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        if (playlist?.Name == null) throw new ArgumentException("Playlist needs a name");

        var (baseUrl, apiKey) = await ConnectionAsync(options, ct);

        // One walk of the audio library resolves the tracks by path, then
        // by the metadata the walk already carries when the paths do not
        // line up. Only items the library does not hold at all (a file the
        // server has not scanned yet) reach the per-track search below, so
        // a normal sync costs a handful of requests instead of several per
        // track.
        var library = await LoadLibraryIndexAsync(baseUrl, apiKey, ct);

        var itemIds = new List<string>();
        var likedItemIds = new List<string>();
        var unresolved = new List<string>();
        var byPath = 0;
        var byMetadata = 0;
        var searched = 0;

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

                string? itemId = null;
                if (library.Find(lookupPath) is { } pathHit)
                {
                    itemId = pathHit;
                    byPath++;
                }
                else if (library.FindByMetadata(track) is { } metaHit)
                {
                    // The library holds the track under another path (a
                    // different mount, a file renamed after the scan): its
                    // own metadata already names it, so no search request.
                    itemId = metaHit;
                    byMetadata++;
                }
                else
                {
                    searched++;
                    itemId = await ResolveItemIdBySearchAsync(
                        baseUrl, apiKey, lookupPath, track, library.Loaded, ct);
                }

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

        _logger.LogInformation(
            "Jellyfin sync for '{Name}': {Resolved} of {Total} tracks resolved "
            + "({ByPath} by path, {ByMetadata} by metadata, {Searched} needed a search)",
            playlist.Name, itemIds.Count, playlist.Tracks?.Count ?? 0, byPath, byMetadata, searched);

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
    /// The audio library of the server as a path -> item id lookup, plus a
    /// title -> items lookup for the tracks whose path does not line up.
    /// The server answers with its own view of the paths; the playlist's
    /// remap rules point ours at the same names, and a path the rules miss
    /// still resolves through the metadata the walk carries. Loaded: false
    /// when the walk could not be made at all (the per-track lookups then
    /// fall back to the search route, which is also what older servers
    /// need).
    /// </summary>
    private sealed record LibraryIndex(
        Dictionary<string, string> ByPath,
        Dictionary<string, List<JellyfinItem>> ByTitle,
        bool Loaded)
    {
        public string? Find(string path)
            => ByPath.TryGetValue(NormalizePath(path), out var id) ? id : null;

        /// <summary>
        /// Finds the item this track names, for when the library holds the
        /// file under a path the remap rules do not reach (a different
        /// Docker mount, a rename after the scan). The title is the index
        /// key; the artist and runtime pick the right item when several
        /// share it. A title the index cannot settle, because several
        /// items share it without a separating signal or because the one
        /// item contradicts the track, is left to the server search.
        /// </summary>
        public string? FindByMetadata(Track track)
        {
            if (string.IsNullOrWhiteSpace(track.Title)) return null;
            if (!ByTitle.TryGetValue(track.Title.Trim(), out var candidates) || candidates.Count == 0)
                return null;
            var best = PickBest(candidates, track);
            if (best?.Id is not { Length: > 0 } id) return null;
            // The only item with this exact title is still the wrong
            // recording when its artist or length contradicts the track
            // (the server may hold the right one under a longer title),
            // so that goes to the search too. Several items always need a
            // positive signal.
            if (candidates.Count == 1)
                return DisagreesWith(best, track) ? null : id;
            return ScoreItem(best, track) > 0 ? id : null;
        }
    }

    /// <summary>
    /// How long a loaded library index serves later playlists. A sync job
    /// runs one sync per playlist back to back, and walking a large
    /// library once per playlist would cost more than the old per-track
    /// lookups. A minute is short enough that a file scanned mid-job is
    /// found by the per-track search fallback on the next job.
    /// </summary>
    internal static TimeSpan LibraryIndexTtl { get; set; } = TimeSpan.FromSeconds(60);

    private readonly object _libraryCacheGate = new();
    private (string Key, DateTime FetchedAtUtc, LibraryIndex Index)? _libraryCache;

    private async Task<LibraryIndex> LoadLibraryIndexAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var key = $"{baseUrl}\u0000{apiKey}";
        lock (_libraryCacheGate)
        {
            if (_libraryCache is { } cached
                && cached.Key == key
                && DateTime.UtcNow - cached.FetchedAtUtc < LibraryIndexTtl)
            {
                return cached.Index;
            }
        }

        var items = await LoadItemsAsync(
            baseUrl, apiKey, "Recursive=true&IncludeItemTypes=Audio&Fields=Path", ct);
        if (items is null)
        {
            // A failed walk is not cached: the next playlist retries instead
            // of degrading to search fallbacks for the whole window.
            return new LibraryIndex(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, List<JellyfinItem>>(StringComparer.OrdinalIgnoreCase),
                Loaded: false);
        }

        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var byTitle = new Dictionary<string, List<JellyfinItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Id is not { Length: > 0 })
                continue;
            if (item.Path is { Length: > 0 } path)
                byPath.TryAdd(NormalizePath(path), item.Id);
            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                var title = item.Name.Trim();
                if (!byTitle.TryGetValue(title, out var list))
                    byTitle[title] = list = new List<JellyfinItem>();
                list.Add(item);
            }
        }
        var index = new LibraryIndex(byPath, byTitle, Loaded: true);
        lock (_libraryCacheGate)
        {
            _libraryCache = (key, DateTime.UtcNow, index);
        }
        _logger.LogDebug(
            "Jellyfin library index: {Items} audio items, {Paths} with a path, {Titles} titled",
            items.Count, byPath.Count, byTitle.Count);
        if (items.Count > 0 && byPath.Count == 0)
        {
            // Audio items came back without a single file path: the
            // profile's user may lack the permission that reveals paths,
            // or the server omits them. The title index still resolves
            // the tracks, so the sync stays correct, just without the
            // exact path check.
            _logger.LogInformation(
                "Jellyfin returned no file paths for {Items} library items; matching tracks by metadata. "
                + "Check the profile user's permissions",
                items.Count);
        }
        return index;
    }

    /// <summary>
    /// One paged walk of an item query ("{route}?{query}"). Null when the
    /// server refused the first page; a later failed page keeps what was
    /// read so far instead of dropping the whole list.
    /// </summary>
    private async Task<List<JellyfinItem>?> LoadItemsAsync(
        string baseUrl, string apiKey, string query, CancellationToken ct, string route = "/Items")
    {
        const int pageSize = 1000;
        var items = new List<JellyfinItem>();
        var startIndex = 0;
        try
        {
            while (true)
            {
                using var resp = await SendAsync(baseUrl, apiKey,
                    $"{route}?{query}&EnableTotalRecordCount=false&StartIndex={startIndex}&Limit={pageSize}",
                    ct: ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Jellyfin answered {Status} for the item query '{Query}'", resp.StatusCode, query);
                    return startIndex == 0 ? null : items;
                }
                var page = await resp.Content.ReadFromJsonAsync<JellyfinItemsResult>(cancellationToken: ct);
                var pageItems = page?.Items;
                if (pageItems is not { Length: > 0 }) return items;
                items.AddRange(pageItems);
                if (pageItems.Length < pageSize) return items;
                startIndex += pageItems.Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jellyfin item query failed for '{Query}'", query);
            return startIndex == 0 ? null : items;
        }
    }

    /// <summary>
    /// Resolves one local path to a Jellyfin item id when the library walk
    /// did not already resolve it: the exact-path route on older servers
    /// (10.11+ answers 400 for it), then the metadata search. Returns null
    /// when the server does not know the track.
    /// </summary>
    private async Task<string?> ResolveItemIdBySearchAsync(
        string baseUrl, string apiKey, string lookupPath, Track track, bool libraryLoaded, CancellationToken ct)
    {
        // With a library index, a path that is not in it is genuinely
        // unknown to the server; the path route (dead on current Jellyfin)
        // is only worth a try when no index could be read.
        if (!libraryLoaded)
        {
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
        }

        // Jellyfin does not index file names and its search does not match
        // a combined "title artist" term, so the title search is the one
        // that resolves real tracks. It goes first so a hit costs a single
        // request; the looser terms stay as the fallback for servers that
        // index more than their metadata. The artist and duration pick the
        // right item when the title is not unique.
        var terms = new[]
        {
            track.Title,
            string.Join(' ', new[] { track.Title, track.Artist }.Where(t => !string.IsNullOrEmpty(t))),
            Path.GetFileName(lookupPath)
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
                if (list?.Items is not { Length: > 0 }) continue;
                var best = PickBest(list.Items, track);
                if (best?.Id is { Length: > 0 } id) return id;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Search lookup failed for {term}", term); }
        }
        return null;
    }

    /// <summary>Paths compare on one spelling: separators unified, no trailing slash.</summary>
    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    /// <summary>
    /// The item that fits the track best: a single result wins outright,
    /// otherwise the artist and the duration decide. A title search can
    /// return covers and live versions, so the first hit is not enough.
    /// </summary>
    private static JellyfinItem? PickBest(IReadOnlyList<JellyfinItem> items, Track track)
    {
        if (items.Count == 1) return items[0];

        JellyfinItem? best = null;
        var bestScore = -1;
        foreach (var item in items)
        {
            var score = ScoreItem(item, track);
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }
        return best;
    }

    /// <summary>
    /// How well one library item fits the track. The artist and a close
    /// runtime are the only signals, so an item with neither scores zero.
    /// </summary>
    private static int ScoreItem(JellyfinItem item, Track track)
    {
        var score = 0;
        if (!string.IsNullOrEmpty(track.Artist) && item.Artists is { Length: > 0 }
            && item.Artists.Any(a => a.Contains(track.Artist, StringComparison.OrdinalIgnoreCase)
                || track.Artist.Contains(a, StringComparison.OrdinalIgnoreCase)))
        {
            score += 2;
        }
        if (track.Duration is { } duration && item.RunTimeTicks is { } ticks
            && Math.Abs(TimeSpan.FromTicks(ticks).TotalSeconds - duration.TotalSeconds) <= 5)
        {
            score += 1;
        }
        return score;
    }

    /// <summary>
    /// True when the item carries a field that contradicts the track: the
    /// walk did return that field, so the mismatch is real and the item is
    /// a different recording. A field the item does not carry cannot
    /// disagree, so a sparse walk does not reject valid matches.
    /// </summary>
    private static bool DisagreesWith(JellyfinItem item, Track track)
    {
        if (!string.IsNullOrEmpty(track.Artist) && item.Artists is { Length: > 0 }
            && !item.Artists.Any(a => a.Contains(track.Artist, StringComparison.OrdinalIgnoreCase)
                || track.Artist.Contains(a, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (track.Duration is { } duration && item.RunTimeTicks is { } ticks
            && Math.Abs(TimeSpan.FromTicks(ticks).TotalSeconds - duration.TotalSeconds) > 5)
        {
            return true;
        }
        return false;
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
                // A playlist whose members and order already match stays
                // untouched: rewriting a 1000-item list on every run is
                // the expensive part of an otherwise unchanged sync.
                var current = await GetPlaylistItemIdsAsync(baseUrl, apiKey, existingId, ct);
                if (current is not null && current.SequenceEqual(itemIds))
                {
                    _logger.LogInformation("Playlist '{name}' is unchanged ({Count} items)", name, itemIds.Count);
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                        existingId, "Playlist unchanged");
                    return;
                }

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
                        existingId, $"Update failed with {update.StatusCode}", Succeeded: false);
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
                    null, $"Create failed with {resp.StatusCode}", Succeeded: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Jellyfin API to upsert playlist");
            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                null, $"Error: {ex.Message}", Succeeded: false);
        }

        if (_lastReport == null)
            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(),
                null, "Completed with warnings");
    }

    /// <summary>
    /// The member item ids of an existing playlist (GET /Playlists/{id}),
    /// or null when the server does not answer that route (API-key calls
    /// may be refused a playlist read): the caller then writes like before.
    /// </summary>
    private async Task<List<string>?> GetPlaylistItemIdsAsync(
        string baseUrl, string apiKey, string playlistId, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(baseUrl, apiKey,
                $"/Playlists/{Uri.EscapeDataString(playlistId)}", ct: ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jellyfin answered {Status} reading playlist {Id}", resp.StatusCode, playlistId);
                return null;
            }
            var detail = await resp.Content.ReadFromJsonAsync<JellyfinPlaylistDetail>(cancellationToken: ct);
            return detail?.ItemIds?.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playlist read failed for {Id}", playlistId);
            return null;
        }
    }

    /// <summary>
    /// Marks the given items as favorites for the configured Jellyfin user
    /// (POST /Users/{userId}/FavoriteItems/{itemId}, with the older
    /// /Users/{userId}/Items/{itemId}/Favorite route as fallback). The
    /// user's current favorites are read once, so an unchanged liked song
    /// is not re-posted on every run.
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

        var favorites = await LoadFavoriteIdsAsync(baseUrl, apiKey, userId, ct);

        var marked = 0;
        var already = 0;
        foreach (var itemId in likedItemIds)
        {
            if (favorites?.Contains(itemId) == true)
            {
                already++;
                continue;
            }
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
        _logger.LogInformation(
            "Marked {Marked}/{Total} liked songs as Jellyfin favorites ({Already} were already marked)",
            marked, likedItemIds.Count, already);
    }

    /// <summary>
    /// The item ids the user already marked as favorites; null when the
    /// server does not answer the query (the caller then posts like before).
    /// </summary>
    private async Task<HashSet<string>?> LoadFavoriteIdsAsync(
        string baseUrl, string apiKey, string userId, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(userId);
        // The per-user route is the historical spelling and what the
        // settings wizard's test connection reads; the modern Item query
        // with an explicit userId is the fallback.
        var items = await LoadItemsAsync(baseUrl, apiKey,
            "Recursive=true&IncludeItemTypes=Audio&Filters=IsFavorite", ct,
            route: $"/Users/{id}/Items");
        items ??= await LoadItemsAsync(baseUrl, apiKey,
            $"Recursive=true&IncludeItemTypes=Audio&Filters=IsFavorite&userId={id}", ct);
        return items?
            .Where(i => i.Id is { Length: > 0 })
            .Select(i => i.Id!)
            .ToHashSet(StringComparer.Ordinal);
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

    private class JellyfinItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string[]? Artists { get; set; }
        public long? RunTimeTicks { get; set; }
    }
    private class JellyfinItemsResult { public JellyfinItem[]? Items { get; set; } }
    private class JellyfinPlaylist { public string? Id { get; set; } public string? Name { get; set; } }
    private class JellyfinPlaylistDetail { public string[]? ItemIds { get; set; } }
}
