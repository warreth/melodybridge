using System.Collections.Concurrent;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Resolves a track's ISRC through Deezer's public search API when the
/// playlist source does not provide one (the Spotify embed scrape,
/// YouTube, CSV imports). Deezer returns the ISRC of every hit and needs
/// no account. When Deezer has no confident match, MusicBrainz is asked
/// as a fallback (one request per second, a real User-Agent, no key).
///
/// Hits are ranked with the same rules the plugins use: title/artist
/// similarity, duration proximity and no unrequested modifiers. Only a
/// High-confidence match is accepted, because a wrong ISRC would steer
/// the ISRC-first searches to the wrong recording; no ISRC is better
/// than a wrong one.
/// </summary>
public class IsrcResolver
{
    private const string DeezerSearchBase = "https://api.deezer.com/search";
    private const string MusicBrainzSearchBase = "https://musicbrainz.org/ws/2/recording";

    private readonly HttpClient _http;
    private readonly ILogger<IsrcResolver> _logger;

    /// <summary>The fallback client; null keeps Deezer as the only source.</summary>
    private readonly HttpClient? _musicBrainz;

    // MusicBrainz asks for at most one request per second; the fallback
    // path is rare, but the gate keeps a burst of misses polite.
    private readonly SemaphoreSlim _musicBrainzGate = new(1, 1);
    private DateTimeOffset _lastMusicBrainzCall = DateTimeOffset.MinValue;

    // Memoized verdicts, null included: a track that resolves once keeps
    // its answer, and a miss is not retried on every download attempt. The
    // resolver is a singleton, so the cache needs a ceiling; clearing it
    // costs a few lookups, never correctness.
    private const int MaxCacheEntries = 4096;
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public IsrcResolver(HttpClient http, ILogger<IsrcResolver> logger, HttpClient? musicBrainz = null)
    {
        _http = http;
        _logger = logger;
        _musicBrainz = musicBrainz;
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
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();

        var resolved = await ResolveUncachedAsync(artist, title, duration, ct);
        _cache[key] = resolved;
        return resolved;
    }

    private async Task<string?> ResolveUncachedAsync(
        string artist, string title, TimeSpan? duration, CancellationToken ct)
    {
        var deezer = await TryDeezerAsync(artist, title, duration, ct);
        if (deezer is not null) return deezer;

        // Deezer missed: MusicBrainz has a wider independent catalogue.
        return await TryMusicBrainzAsync(artist, title, duration, ct);
    }

    private async Task<string?> TryDeezerAsync(
        string artist, string title, TimeSpan? duration, CancellationToken ct)
    {
        try
        {
            var query = string.Join(" ",
                new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            using var resp = await _http.GetAsync(
                $"{DeezerSearchBase}?q={Uri.EscapeDataString(query)}&limit=5", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deezer ISRC lookup returned {Status} for '{Query}'",
                    (int)resp.StatusCode, query);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var isrc = ParseBestIsrc(body, artist, title, duration);
            if (isrc is not null)
                _logger.LogDebug("Resolved ISRC {Isrc} for '{Artist} - {Title}' via Deezer", isrc, artist, title);
            return isrc;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deezer ISRC lookup failed for '{Artist} - {Title}'", artist, title);
            return null;
        }
    }

    private async Task<string?> TryMusicBrainzAsync(
        string artist, string title, TimeSpan? duration, CancellationToken ct)
    {
        if (_musicBrainz is null) return null;

        try
        {
            await _musicBrainzGate.WaitAsync(ct);
            try
            {
                var wait = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - _lastMusicBrainzCall);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastMusicBrainzCall = DateTimeOffset.UtcNow;

                var query = Uri.EscapeDataString($"artist:\"{artist}\" AND recording:\"{title}\"");
                using var resp = await _musicBrainz.GetAsync(
                    $"{MusicBrainzSearchBase}?query={query}&fmt=json&limit=5", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("MusicBrainz ISRC lookup returned {Status} for '{Artist} - {Title}'",
                        (int)resp.StatusCode, artist, title);
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                var isrc = ParseBestMusicBrainzIsrc(body, artist, title, duration);
                if (isrc is not null)
                    _logger.LogDebug("Resolved ISRC {Isrc} for '{Artist} - {Title}' via MusicBrainz",
                        isrc, artist, title);
                return isrc;
            }
            finally
            {
                _musicBrainzGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MusicBrainz ISRC lookup failed for '{Artist} - {Title}'", artist, title);
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

    /// <summary>
    /// Parses a MusicBrainz recording search response and returns the ISRC
    /// of the best High-confidence candidate. Internal for tests.
    /// </summary>
    internal static string? ParseBestMusicBrainzIsrc(
        string json, string artist, string title, TimeSpan? expectedDuration)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recordings", out var recordings) ||
                recordings.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<DownloaderSearchHit>();
            foreach (var item in recordings.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var isrc = FirstIsrc(item);
                if (string.IsNullOrWhiteSpace(isrc)) continue;

                var hitTitle = GetString(item, "title");
                var hitArtist = FirstArtistCredit(item);
                var lengthMs = item.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number
                    ? l.GetDouble()
                    : (double?)null;

                candidates.Add(new DownloaderSearchHit(
                    Title: hitTitle ?? title,
                    Artist: hitArtist ?? artist,
                    SourceUrl: isrc,
                    Duration: lengthMs is > 0 ? TimeSpan.FromMilliseconds(lengthMs.Value) : null,
                    MatchConfidence: FuzzyMatcher.Confidence(artist, title, hitArtist, hitTitle)));
            }

            var best = SearchRanker.Rank(artist, title, expectedDuration, candidates);
            // Same rule as the Deezer path: a wrong ISRC is worse than none.
            if (best is not { MatchConfidence: MatchConfidence.High }) return null;
            if (!FuzzyMatcher.DurationPlausible(expectedDuration, best.Duration)) return null;
            return best.SourceUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstIsrc(JsonElement item)
    {
        if (!item.TryGetProperty("isrcs", out var isrcs) || isrcs.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var value in isrcs.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string? FirstArtistCredit(JsonElement item)
    {
        if (!item.TryGetProperty("artist-credit", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(credit, "name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
            if (credit.TryGetProperty("artist", out var artistObj))
            {
                var nested = GetString(artistObj, "name");
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
