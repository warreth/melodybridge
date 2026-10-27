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
        IReadOnlyList<string> Tags)
    {
        public string SourceUrl => $"https://tidal.com/browse/track/{Id}";
    }

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
        return SearchCachedAsync("isrc:" + code + ":" + NormalizeCountry(countryCode), url, ct);
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
        return SearchCachedAsync("text:" + text + ":" + NormalizeCountry(countryCode), url, ct);
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

            tracks.Add(new TidalPublicTrack(
                Id: id,
                Title: title!,
                Artist: GetArtist(item),
                Duration: duration,
                Isrc: GetString(item, "isrc"),
                StreamReady: streamReady,
                AudioQuality: GetString(item, "audioQuality"),
                Tags: GetTags(item)));
        }
        return tracks;
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

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeCountry(string? country)
        => string.IsNullOrWhiteSpace(country) ? "US" : country.Trim().ToUpperInvariant();
}
