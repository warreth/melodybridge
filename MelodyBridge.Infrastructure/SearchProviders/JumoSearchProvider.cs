using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Jumo;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches the Jumo service for the Jumo downloader. The service's
/// catalogue is Qobuz, which indexes ISRCs, so the code runs first when the
/// query has one; the cleaned text is the fallback. A refused or throttled
/// request is reported to <see cref="JumoSession"/> so the waterfall skips
/// the plugin for the short negative window.
/// </summary>
public sealed class JumoSearchProvider : ISearchProvider
{
    public const string ProviderId = "jumo-catalogue";
    public const string DownloaderId = JumoSession.PluginId;

    /// <summary>
    /// Search timeout: inclusive of the client's own throttle backoff,
    /// which can span half a minute during a limiter burst. Still well
    /// inside the plugin budget.
    /// </summary>
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

    private readonly JumoSession _session;
    private readonly ILogger<JumoSearchProvider> _logger;

    public JumoSearchProvider(JumoSession session, ILogger<JumoSearchProvider> logger)
    {
        _session = session;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "Jumo catalogue";
    public string Description => "Searches the Jumo catalogue, ISRC first.";
    public SearchData Data => SearchData.Text | SearchData.Isrc;
    public SearchTier Tier => SearchTier.UnofficialCatalog;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Jumo ISRC-first search",
            Tier: SearchTier.UnofficialCatalog) };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        try
        {
            var region = await _session.EffectiveRegionAsync(ct);

            foreach (var term in SearchQueries(query))
            {
                // A small page keeps the request light; the service throttles
                // heavy API use (live probing saw 503s for a limit of 30 while
                // smaller pages passed in the same window).
                var url = JumoClient.Origin + JumoClient.SearchPath
                    + "?query=" + Uri.EscapeDataString(term)
                    + "&offset=0&limit=10&region=" + Uri.EscapeDataString(region) + "&view=ui";

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SearchTimeout);
                var response = await _session.Client.ApiGetAsync(url, timeout.Token);
                if (response.Status != 200)
                {
                    _logger.LogDebug("Jumo search returned {Status}", response.Status);

                    // A refused or throttled search means the service is not
                    // usable right now. Report that as unavailable (the short
                    // negative TTL makes the waterfall skip the plugin for a
                    // while) instead of letting every track of a run fail as a
                    // bare "no source" miss. No second query is tried: the
                    // next one would be throttled too.
                    if (response.Status is 403 or 429 or 503)
                        _session.ReportSearchFailure(response.Status);
                    return null;
                }

                // An ISRC term only counts when the response carries that
                // exact code; otherwise the text term gets its turn.
                var hit = ParseSearchTracks(response.Body, query.Artist, query.Title,
                    query.ExpectedDuration, query.Isrc,
                    requireIsrcMatch: query.HasIsrc && term == query.Isrc);
                if (hit is not null) return hit;
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Jumo search timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jumo search '{Query}' failed", searchText);
            return null;
        }
    }

    /// <summary>
    /// The search terms in order: the ISRC first when the query carries
    /// one (the service's catalogue is Qobuz, which indexes the code),
    /// then the cleaned text. The first term with a hit wins, so a track
    /// whose text search drowns in soundtracks still finds its recording.
    /// </summary>
    internal static IReadOnlyList<string> SearchQueries(TrackQuery query)
    {
        var terms = new List<string>(2);
        if (query.HasIsrc) terms.Add(query.Isrc!);
        var text = query.SearchText;
        if (text.Length > 0 && !terms.Contains(text, StringComparer.Ordinal)) terms.Add(text);
        return terms;
    }

    /// <summary>
    /// Ranks the search response's track items with the shared ranker and
    /// maps them onto source identifiers. The album id travels along so
    /// the download can refetch fresh descriptor URLs later.
    /// </summary>
    internal static DownloaderSearchHit? ParseSearchTracks(
        string json, string artist, string fallbackTitle,
        TimeSpan? expectedDuration = null, string? expectedIsrc = null,
        bool requireIsrcMatch = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<(DownloaderSearchHit Hit, string? Isrc, bool Hires)>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var trackId = JumoJson.GetId(item, "id");
                var albumId = item.TryGetProperty("album", out var album) ? JumoJson.GetId(album, "id") : null;
                if (trackId is null || albumId is null) continue;

                var hitTitle = JumoJson.GetString(item, "title") ?? fallbackTitle;
                var hitArtist = JumoJson.GetPrimaryArtist(item);

                TimeSpan? duration = item.TryGetProperty("duration", out var d) &&
                                     d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetDouble())
                    : null;

                var hires = item.TryGetProperty("hires", out var h) && h.ValueKind == JsonValueKind.True;
                var hit = new DownloaderSearchHit(
                    Title: hitTitle,
                    Artist: hitArtist,
                    SourceUrl: JumoSourceId.Build(albumId, trackId),
                    Duration: duration,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
                candidates.Add((hit, JumoJson.GetString(item, "isrc"), hires));
            }

            if (candidates.Count == 0) return null;

            // An exact ISRC is the recording itself, not a guess: the
            // catalogue's title wording may differ, so the code carries
            // that pool while the length still has to be plausible.
            var isrcPool = candidates
                .Where(c => IsrcEquals(expectedIsrc, c.Isrc))
                .ToList();
            if (isrcPool.Count > 0)
                return SearchRanker.RankExactRecording(artist, fallbackTitle,
                    expectedDuration, isrcPool.Select(c => c.Hit),
                    tieBreaker: hit => isrcPool.First(p => ReferenceEquals(p.Hit, hit)).Hires ? 1 : 0);
            if (requireIsrcMatch) return null;

            return SearchRanker.Rank(artist, fallbackTitle, expectedDuration,
                candidates.Select(c => c.Hit),
                tieBreaker: hit => candidates.First(p => ReferenceEquals(p.Hit, hit)).Hires ? 1 : 0);
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
}
