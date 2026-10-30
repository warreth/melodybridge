using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches the Monochrome API's /search/tracks endpoint. The index is
/// literal, so the query carries the cleaned terms; every returned item is
/// ranked, with an exact ISRC narrowing the pool to that recording even
/// when the title wording differs.
/// </summary>
public sealed class MonochromeSearchProvider : ISearchProvider
{
    public const string ProviderId = "monochrome-catalogue";
    public const string DownloaderId = "monochrome";

    /// <summary>Per-request deadline so one hanging search cannot stall the waterfall.</summary>
    private static readonly TimeSpan SearchRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly IPluginConfig _config;
    private readonly ILogger<MonochromeSearchProvider> _logger;

    /// <summary>The last transport verdict, for the live diagnostics and the card.</summary>
    public string? LastFailure { get; private set; }

    public MonochromeSearchProvider(
        HttpClient http, IPluginConfig config, ILogger<MonochromeSearchProvider> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "Monochrome catalogue";
    public string Description => "Searches the Monochrome catalogue, ISRC first.";
    public SearchData Data => SearchData.Text | SearchData.Isrc;
    public SearchTier Tier => SearchTier.UnofficialCatalog;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Monochrome ISRC-first search",
            Tier: SearchTier.UnofficialCatalog) };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        // The catalogue returns an ISRC on every hit. Ask for the code
        // first when the query carries one; a service that does not index
        // codes answers with noise, the strict pass rejects it, and the
        // cleaned text below gets its turn.
        if (query.HasIsrc)
        {
            var byIsrc = await SearchTextAsync(query.Isrc!, query, requireIsrcMatch: true, ct);
            if (byIsrc is not null) return byIsrc;
        }

        // Cleaned terms: the API's index is literal, so "(Original Mix)"
        // suffixes and featuring tags reduce matches before the request is
        // even sent.
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;
        return await SearchTextAsync(searchText, query, requireIsrcMatch: false, ct);
    }

    private async Task<DownloaderSearchHit?> SearchTextAsync(
        string searchText, TrackQuery query, bool requireIsrcMatch,
        CancellationToken ct)
    {
        var baseUrl = await MonochromeApi.BaseUrlAsync(_config, DownloaderId, ct);
        var url = baseUrl + "/search/tracks?q=" + Uri.EscapeDataString(searchText) + "&limit=10";
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(SearchRequestTimeout);

            using var resp = await _http.GetAsync(url, request.Token);
            if (!resp.IsSuccessStatusCode)
            {
                LastFailure = $"HTTP {(int)resp.StatusCode}";
                _logger.LogDebug("Monochrome search returned {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(request.Token);
            var hit = ParseSearchTracks(body, baseUrl, query.Artist, query.Title,
                query.ExpectedDuration, query.Isrc, requireIsrcMatch);
            LastFailure = hit is null ? "no usable result" : null;
            return hit;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastFailure = "timed out";
            _logger.LogDebug("Monochrome search timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastFailure = ex.Message;
            _logger.LogDebug(ex, "Monochrome search '{Query}' failed", searchText);
            return null;
        }
    }

    /// <summary>
    /// Parses a /search/tracks response ({"tracks":[...]}) into the best hit:
    /// every item is ranked by title/artist confidence plus duration
    /// proximity. An exact ISRC match (when the query carries one) narrows
    /// the pool to the recording itself and is accepted on that code even
    /// when the catalogue spells the title differently, so a noisy title
    /// cannot pick a different master. Null when there are no usable items.
    /// </summary>
    internal static DownloaderSearchHit? ParseSearchTracks(
        string json, string baseUrl, string artist, string fallbackTitle,
        TimeSpan? expectedDuration = null, string? expectedIsrc = null,
        bool requireIsrcMatch = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array ||
                tracks.GetArrayLength() == 0)
                return null;

            // Rank every item instead of trusting the API's first row:
            // the list routinely buries the real match under remixes.
            var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
            foreach (var item in tracks.EnumerateArray())
            {
                // One malformed entry must not poison the response: skip
                // the item, keep the loop.
                if (item.ValueKind != JsonValueKind.Object) continue;

                // Unplayable entries (takedowns, region blocks) would only
                // fail later, after a wasted download attempt.
                if (item.TryGetProperty("playable", out var playable) &&
                    playable.ValueKind == JsonValueKind.False)
                    continue;

                var id = GetString(item, "id") ?? GetString(item, "trackId");
                if (string.IsNullOrEmpty(id)) continue;

                var hitTitle = GetString(item, "title") ?? fallbackTitle;
                var hitArtist = GetPrimaryArtist(item);

                TimeSpan? duration = item.TryGetProperty("duration", out var d) &&
                                     d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromMilliseconds(d.GetDouble())
                    : null;

                var hit = new DownloaderSearchHit(
                    Title: hitTitle,
                    Artist: hitArtist,
                    SourceUrl: $"{baseUrl}/track/{id}",
                    Duration: duration,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
                candidates.Add((hit, GetString(item, "isrc")));
            }

            if (candidates.Count == 0) return null;

            // An exact ISRC is the recording itself, not a guess: when the
            // query carries one and any candidate matches, the others are
            // a different master even when their titles look closer.
            var isrcPool = candidates.Where(c => IsrcEquals(expectedIsrc, c.Isrc)).ToList();
            if (isrcPool.Count > 0)
                return SearchRanker.RankExactRecording(artist, fallbackTitle,
                    expectedDuration, isrcPool.Select(c => c.Hit));
            if (requireIsrcMatch) return null;

            return SearchRanker.Rank(artist, fallbackTitle, expectedDuration,
                candidates.Select(c => c.Hit));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when both ISRCs are present and equal, ignoring case and padding.</summary>
    internal static bool IsrcEquals(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
           && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Primary artist: artistNames[0], artists[0].name or artist.name.</summary>
    private static string? GetPrimaryArtist(JsonElement item)
    {
        if (item.TryGetProperty("artistNames", out var names) &&
            names.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in names.EnumerateArray())
                if (name.ValueKind == JsonValueKind.String)
                    return name.GetString();
        }

        if (item.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
            return GetString(artists[0], "name");

        if (item.TryGetProperty("artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString();
            var name = GetString(artist, "name");
            if (!string.IsNullOrEmpty(name)) return name;
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
