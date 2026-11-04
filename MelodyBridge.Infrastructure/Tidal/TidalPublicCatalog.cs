using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// The public Tidal catalogue search used by the search provider. It asks
/// the same ODSkyler token worker the Rhythmax frontend uses for a Tidal
/// access token, then searches Tidal's v1 API by ISRC first (the exact
/// recording) and by text as the fallback when no ISRC is known or the code
/// has no result. This is deliberately separate from <see cref="TidalClient"/>:
/// that one uses the user's own tiddl session, this one needs no account and
/// serves both Tidal download sources.
/// </summary>
public class TidalPublicCatalog
{
    public const string DefaultTokenUrl = "https://auth.odskyler.workers.dev/token";
    private const string ApiBase = "https://api.tidal.com/v1";

    public enum SearchStatus
    {
        Ok,
        /// <summary>Tidal refused the public token (401 or 403).</summary>
        Rejected,
        /// <summary>Tidal asked us to slow down (429).</summary>
        RateLimited,
        /// <summary>The API or the token endpoint did not answer.</summary>
        Unreachable,
        /// <summary>The playlist does not exist or is not public.</summary>
        NotFound,
    }

    public record SearchResult(
        IReadOnlyList<TidalPublicTrack> Tracks,
        SearchStatus Status,
        string? Message = null)
    {
        public static readonly IReadOnlyList<TidalPublicTrack> Empty = Array.Empty<TidalPublicTrack>();

        public static SearchResult Ok(IReadOnlyList<TidalPublicTrack> tracks) =>
            new(tracks, SearchStatus.Ok);
    }

    /// <summary>One Tidal catalogue track, reduced to what search and ranking need.</summary>
    public record TidalPublicTrack(
        long Id,
        string Title,
        string Artist,
        TimeSpan? Duration,
        string? Isrc,
        bool StreamReady,
        string? AudioQuality,
        IReadOnlyList<string> Tags,
        string? Album = null,
        string? AlbumArtist = null,
        string? CoverUrl = null)
    {
        public string SourceUrl => $"https://tidal.com/browse/track/{Id}";
    }

    /// <summary>One public Tidal playlist and its tracks.</summary>
    public record TidalPlaylist(
        string Id,
        string Title,
        string? Description,
        string? Owner,
        string? CoverUrl,
        int TrackCount,
        TimeSpan? Duration);

    public record TidalPlaylistResult(
        TidalPlaylist? Playlist,
        IReadOnlyList<TidalPublicTrack> Tracks,
        SearchStatus Status,
        string? Message = null);

    private sealed record CachedSearch(DateTimeOffset ExpiresAt, SearchResult Result);

    private readonly HttpClient _http;
    private readonly ILogger<TidalPublicCatalog> _logger;
    private readonly ConcurrentDictionary<string, CachedSearch> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public TidalPublicCatalog(HttpClient http, ILogger<TidalPublicCatalog> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Tracks carrying one ISRC. An exact-ISRC lookup is one request and
    /// does not depend on a text query being clean enough.
    /// </summary>
    public Task<SearchResult> SearchByIsrcAsync(
        string isrc, string countryCode, int limit = 10, CancellationToken ct = default)
    {
        var code = isrc.Trim();
        if (code.Length == 0)
            return Task.FromResult(new SearchResult(SearchResult.Empty, SearchStatus.Ok));
        var url = $"{ApiBase}/tracks?isrc={Uri.EscapeDataString(code)}"
            + $"&limit={Math.Clamp(limit, 1, 50)}&offset=0"
            + $"&countryCode={Uri.EscapeDataString(NormalizeCountry(countryCode))}";
        return SearchCachedAsync(
            $"isrc:{code}:{NormalizeCountry(countryCode)}:{Math.Clamp(limit, 1, 50)}", url, ct);
    }

    /// <summary>The text fallback, used only when the ISRC route had no result.</summary>
    public Task<SearchResult> SearchTextAsync(
        string query, string countryCode, int limit = 10, CancellationToken ct = default)
    {
        var text = query.Trim();
        if (text.Length == 0)
            return Task.FromResult(new SearchResult(SearchResult.Empty, SearchStatus.Ok));
        var url = $"{ApiBase}/search/tracks?query={Uri.EscapeDataString(text)}"
            + $"&limit={Math.Clamp(limit, 1, 50)}&offset=0"
            + $"&countryCode={Uri.EscapeDataString(NormalizeCountry(countryCode))}";
        return SearchCachedAsync(
            $"text:{text}:{NormalizeCountry(countryCode)}:{Math.Clamp(limit, 1, 50)}", url, ct);
    }

    /// <summary>
    /// Reads one public playlist and all its track pages. The same public
    /// token the search uses is enough; no Tidal account is involved.
    /// </summary>
    public async Task<TidalPlaylistResult> GetPlaylistAsync(
        string playlistId, string countryCode, CancellationToken ct = default)
    {
        var id = playlistId.Trim();
        if (id.Length == 0)
            return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                SearchStatus.NotFound, "No Tidal playlist id was given.");

        var country = NormalizeCountry(countryCode);
        var token = await TokenAsync(false, ct);
        if (token is null)
            return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                SearchStatus.Unreachable, "The Tidal token endpoint did not answer.");

        try
        {
            var metadataUrl = $"{ApiBase}/playlists/{Uri.EscapeDataString(id)}"
                              + $"?countryCode={Uri.EscapeDataString(country)}";
            var response = await SendAsync(metadataUrl, token, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                token = await TokenAsync(true, ct);
                if (token is null)
                    return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                        SearchStatus.Rejected, "Tidal rejected the public token.");
                response = await SendAsync(metadataUrl, token, ct);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                        SearchStatus.NotFound, "Tidal did not find that public playlist.");
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                        SearchStatus.Rejected, "Tidal rejected the public token.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                        SearchStatus.RateLimited, "Tidal rate limited the playlist request.");
                if (!response.IsSuccessStatusCode)
                    return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                        SearchStatus.Unreachable,
                        $"The Tidal API answered HTTP {(int)response.StatusCode}.");

                using var metadataDoc = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(ct));
                var root = metadataDoc.RootElement;
                var title = GetString(root, "title") ?? "Tidal playlist";
                var owner = root.TryGetProperty("creator", out var creator)
                    ? GetString(creator, "name")
                    : null;
                var cover = ImageUrl(GetString(root, "squareImage")
                                    ?? GetString(root, "image"));
                var trackCount = GetInt(root, "numberOfTracks") ?? 0;
                var duration = GetDouble(root, "duration") is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : (TimeSpan?)null;

                var tracks = new List<TidalPublicTrack>();
                var offset = 0;
                var total = trackCount;
                while (offset < 20_000)
                {
                    var tracksUrl = $"{ApiBase}/playlists/{Uri.EscapeDataString(id)}/tracks"
                                    + $"?countryCode={Uri.EscapeDataString(country)}"
                                    + $"&limit=50&offset={offset}";
                    using var pageResponse = await SendAsync(tracksUrl, token, ct);
                    if (!pageResponse.IsSuccessStatusCode)
                    {
                        if (pageResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                            return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                                SearchStatus.Rejected, "Tidal rejected the playlist track request.");
                        if (pageResponse.StatusCode == HttpStatusCode.TooManyRequests)
                            return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                                SearchStatus.RateLimited, "Tidal rate limited the playlist tracks.");
                        return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                            SearchStatus.Unreachable,
                            $"The Tidal API answered HTTP {(int)pageResponse.StatusCode} for the tracks.");
                    }

                    using var pageDoc = JsonDocument.Parse(
                        await pageResponse.Content.ReadAsStreamAsync(ct));
                    var page = ParseTracks(pageDoc.RootElement);
                    tracks.AddRange(page);
                    total = GetInt(pageDoc.RootElement, "totalNumberOfItems") ?? total;
                    if (page.Count == 0 || offset + page.Count >= total) break;
                    offset += page.Count;
                }

                var playlist = new TidalPlaylist(
                    id, title, GetString(root, "description"), owner, cover,
                    total > 0 ? total : tracks.Count, duration);
                return new TidalPlaylistResult(playlist, tracks, SearchStatus.Ok);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tidal playlist fetch failed");
            return new TidalPlaylistResult(null, Array.Empty<TidalPublicTrack>(),
                SearchStatus.Unreachable, "The Tidal API did not answer.");
        }
    }

    private async Task<SearchResult> SearchCachedAsync(string key, string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Result;

        var result = await SearchCoreAsync(url, ct);
        // A successful empty answer is cached too: repeating the same exact
        // ISRC lookup for every waterfall pass is waste. Failures are not
        // cached, so a token refresh or a recovered API is picked up next time.
        if (result.Status == SearchStatus.Ok)
        {
            if (_cache.Count > 128) _cache.Clear();
            _cache[key] = new CachedSearch(DateTimeOffset.UtcNow.AddMinutes(5), result);
        }
        return result;
    }

    private async Task<SearchResult> SearchCoreAsync(string url, CancellationToken ct)
    {
        try
        {
            var token = await TokenAsync(forceRefresh: false, ct);
            if (token is null)
                return new(SearchResult.Empty, SearchStatus.Unreachable,
                    "The Tidal search token endpoint did not answer");

            var response = await SendAsync(url, token, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                token = await TokenAsync(forceRefresh: true, ct);
                if (token is null)
                    return new(SearchResult.Empty, SearchStatus.Rejected,
                        "Tidal rejected the public search token");
                response = await SendAsync(url, token, ct);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new(SearchResult.Empty, SearchStatus.Rejected,
                        "Tidal rejected the public search token");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                    return new(SearchResult.Empty, SearchStatus.RateLimited,
                        wait is > 0
                            ? $"Tidal rate limited the search; retry in {wait:0} seconds"
                            : "Tidal rate limited the search");
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Tidal public search answered {Status}", (int)response.StatusCode);
                    return new(SearchResult.Empty, SearchStatus.Unreachable,
                        $"The Tidal search API answered HTTP {(int)response.StatusCode}");
                }

                using var doc = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(ct));
                return SearchResult.Ok(ParseTracks(doc.RootElement));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tidal public search failed");
            return new(SearchResult.Empty, SearchStatus.Unreachable,
                "The Tidal search API did not answer");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await _http.SendAsync(request, ct);
    }

    private async Task<string?> TokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is { Length: > 0 } && _tokenExpiresAt > DateTimeOffset.UtcNow)
            return _token;

        await _tokenGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is { Length: > 0 } && _tokenExpiresAt > DateTimeOffset.UtcNow)
                return _token;

            using var response = await _http.GetAsync(DefaultTokenUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Tidal token endpoint answered {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;
            var token = root.TryGetProperty("access_token", out var access)
                && access.ValueKind == JsonValueKind.String
                ? access.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(token)) return null;

            var seconds = 3600;
            if (root.TryGetProperty("expires_in", out var expires))
            {
                if (expires.ValueKind == JsonValueKind.Number && expires.TryGetDouble(out var number))
                    seconds = (int)number;
                else if (expires.ValueKind == JsonValueKind.String
                         && int.TryParse(expires.GetString(), out var parsed))
                    seconds = parsed;
            }

            _token = token;
            // Refresh a minute early so a long download never starts on an
            // about-to-expire token.
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, seconds - 60));
            return _token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tidal token request failed");
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>The track items of a v1 response. Internal for tests.</summary>
    internal static IReadOnlyList<TidalPublicTrack> ParseTracks(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<TidalPublicTrack>();

        var tracks = new List<TidalPublicTrack>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = item.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out var parsedId)
                ? parsedId
                : 0;
            if (id <= 0) continue;

            var title = GetString(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var duration = item.TryGetProperty("duration", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetDouble(out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;

            var streamReady = !item.TryGetProperty("streamReady", out var ready)
                || ready.ValueKind != JsonValueKind.False;

            var album = item.TryGetProperty("album", out var albumElement)
                && albumElement.ValueKind == JsonValueKind.Object
                ? albumElement
                : (JsonElement?)null;

            tracks.Add(new TidalPublicTrack(
                Id: id,
                Title: title!,
                Artist: GetArtist(item),
                Duration: duration,
                Isrc: GetString(item, "isrc"),
                StreamReady: streamReady,
                AudioQuality: GetString(item, "audioQuality"),
                Tags: GetTags(item),
                Album: album is { } albumValue ? GetString(albumValue, "title") : null,
                AlbumArtist: GetAlbumArtist(album, item),
                CoverUrl: album is { } coverValue
                    ? ImageUrl(GetString(coverValue, "cover"))
                    : null));
        }
        return tracks;
    }

    private static string GetAlbumArtist(JsonElement? album, JsonElement item)
    {
        if (album is { } value)
        {
            if (value.TryGetProperty("artist", out var artist))
            {
                var name = GetString(artist, "name");
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
            if (value.TryGetProperty("artists", out var artists)
                && artists.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in artists.EnumerateArray())
                {
                    var name = GetString(entry, "name");
                    if (!string.IsNullOrWhiteSpace(name)) return name!;
                }
            }
        }
        return GetArtist(item);
    }

    private static string GetArtist(JsonElement item)
    {
        if (item.TryGetProperty("artist", out var artist))
        {
            var name = GetString(artist, "name");
            if (!string.IsNullOrWhiteSpace(name)) return name!;
        }
        if (item.TryGetProperty("artists", out var artists)
            && artists.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in artists.EnumerateArray())
            {
                var name = GetString(entry, "name");
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
        }
        return "";
    }

    private static IReadOnlyList<string> GetTags(JsonElement item)
    {
        if (!item.TryGetProperty("mediaMetadata", out var metadata)
            || !metadata.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var tag in tags.EnumerateArray())
            if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 } value)
                result.Add(value);
        return result;
    }

    private static int? GetInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? GetDouble(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string? ImageUrl(string? imageId)
        => string.IsNullOrWhiteSpace(imageId)
            ? null
            : $"https://resources.tidal.com/images/{imageId.Replace('-', '/')}/640x640.jpg";

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeCountry(string? country)
        => string.IsNullOrWhiteSpace(country) ? "US" : country.Trim().ToUpperInvariant();
}
