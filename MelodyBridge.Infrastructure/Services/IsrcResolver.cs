using System.Collections.Concurrent;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Resolves a track's ISRC through Deezer's public search API when the
/// playlist source does not provide one (the Spotify embed scrape,
/// YouTube, CSV imports). Deezer returns the ISRC of every hit and needs
/// no account.
///
/// Hits are ranked with the same rules the plugins use: title/artist
/// similarity, duration proximity and no unrequested modifiers. Only a
/// High-confidence match is accepted, because a wrong ISRC would steer
/// the ISRC-first searches to the wrong recording; no ISRC is better
/// than a wrong one.
/// </summary>
public class IsrcResolver
{
    private const string SearchBase = "https://api.deezer.com/search";

    private readonly HttpClient _http;
    private readonly ILogger<IsrcResolver> _logger;

    // Memoized verdicts, null included: a track that resolves once keeps
    // its answer for the process lifetime, and a miss is not retried on
    // every download attempt.
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public IsrcResolver(HttpClient http, ILogger<IsrcResolver> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// The ISRC of the recording that best matches the metadata, or null
    /// when nothing matches confidently or the lookup fails.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string artist, string title, TimeSpan? duration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var key = $"{artist}\u0000{title}\u0000{duration?.TotalSeconds:0}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var resolved = await ResolveUncachedAsync(artist, title, duration, ct);
        _cache[key] = resolved;
        return resolved;
    }

    private async Task<string?> ResolveUncachedAsync(
        string artist, string title, TimeSpan? duration, CancellationToken ct)
    {
        try
        {
            var query = string.Join(" ",
                new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            using var resp = await _http.GetAsync(
                $"{SearchBase}?q={Uri.EscapeDataString(query)}&limit=5", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deezer ISRC lookup returned {Status} for '{Query}'",
                    (int)resp.StatusCode, query);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var isrc = ParseBestIsrc(body, artist, title, duration);
            if (isrc is not null)
                _logger.LogDebug("Resolved ISRC {Isrc} for '{Artist} - {Title}'", isrc, artist, title);
            return isrc;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deezer ISRC lookup failed for '{Artist} - {Title}'", artist, title);
            return null;
        }
    }

    /// <summary>
    /// Parses the Deezer search response and returns the ISRC of the best
    /// High-confidence candidate. Internal for tests.
    /// </summary>
    internal static string? ParseBestIsrc(
        string json, string artist, string title, TimeSpan? expectedDuration)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<DownloaderSearchHit>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var isrc = GetString(item, "isrc");
                if (string.IsNullOrWhiteSpace(isrc)) continue;

                var hitTitle = GetString(item, "title");
                var hitArtist = item.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object
                    ? GetString(a, "name")
                    : null;
                var seconds = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : (double?)null;

                // The ISRC rides in SourceUrl: it is the only non-empty
                // payload a candidate needs for the shared ranker.
                candidates.Add(new DownloaderSearchHit(
                    Title: hitTitle ?? title,
                    Artist: hitArtist ?? artist,
                    SourceUrl: isrc,
                    Duration: seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, title, hitArtist, hitTitle)));
            }

            var best = SearchRanker.Rank(artist, title, expectedDuration, candidates);
            // A wrong ISRC is worse than none: only a confident match
            // travels into the plugin searches. The ranker keeps
            // wrong-length candidates as a last resort for the waterfall;
            // for an ISRC that is a different recording, so drop it too.
            if (best is not { MatchConfidence: MatchConfidence.High }) return null;
            if (!FuzzyMatcher.DurationPlausible(expectedDuration, best.Duration)) return null;
            return best.SourceUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
