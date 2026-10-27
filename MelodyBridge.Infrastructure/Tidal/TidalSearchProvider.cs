using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// Resolves a track query to a Tidal track URL. ISRC first, because that
/// names the recording itself; the text query is only the fallback when the
/// query has no code or the code is not in Tidal's catalogue. Both Tidal
/// download sources use this provider: the tiddl source downloads through
/// the user's own session, the Rhythmax source through the public hifi API.
/// </summary>
public sealed class TidalSearchProvider : ISearchProvider
{
    private const string CountryKey = "country_code";
    private static readonly SearchProviderTarget[] TargetList =
    [
        new("tidal", SearchData.Isrc | SearchData.Text,
            "Track URL for the tiddl downloader"),
        new("rhythmax", SearchData.Isrc | SearchData.Text,
            "Track URL for the no-account Rhythmax downloader"),
    ];

    private readonly TidalPublicCatalog _catalog;
    private readonly IPluginConfig _config;
    private readonly ILogger<TidalSearchProvider> _logger;

    public TidalSearchProvider(
        TidalPublicCatalog catalog,
        IPluginConfig config,
        ILogger<TidalSearchProvider> logger)
    {
        _catalog = catalog;
        _config = config;
        _logger = logger;
    }

    public string Id => "tidal";
    public string Name => "Tidal ISRC search";
    public string Description =>
        "Looks up the recording in Tidal's catalogue by ISRC and falls back to a title search; returns a tidal.com track URL.";
    public SearchData Data => SearchData.Isrc | SearchData.Text;
    public IReadOnlyList<SearchProviderTarget> Targets => TargetList;

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        var country = await CountryAsync(downloaderId, ct);

        if (query.HasIsrc)
        {
            var byIsrc = await _catalog.SearchByIsrcAsync(query.Isrc!, country, 10, ct);
            if (byIsrc.Status == TidalPublicCatalog.SearchStatus.RateLimited)
            {
                _logger.LogDebug("Tidal ISRC search rate limited for '{Isrc}'", query.Isrc);
                return null;
            }
            if (byIsrc.Status == TidalPublicCatalog.SearchStatus.Rejected)
            {
                _logger.LogDebug("Tidal rejected the public search token for '{Isrc}'", query.Isrc);
                return null;
            }

            if (byIsrc.Status == TidalPublicCatalog.SearchStatus.Ok)
            {
                var exact = byIsrc.Tracks
                    .Where(t => t.StreamReady)
                    .Where(t => string.Equals(t.Isrc?.Trim(), query.Isrc!.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (exact.Count > 0)
                {
                    var hit = RankExact(exact, query);
                    if (hit is not null) return hit;
                }
            }
        }

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var byText = await _catalog.SearchTextAsync(searchText, country, 10, ct);
        if (byText.Status != TidalPublicCatalog.SearchStatus.Ok)
        {
            _logger.LogDebug("Tidal text search for '{Query}' ended {Status}: {Message}",
                searchText, byText.Status, byText.Message);
            return null;
        }

        return Rank(byText.Tracks, query);
    }

    /// <summary>
    /// The exact-ISRC route: duration still has to be plausible, but a
    /// different title spelling is not a reason to reject the recording.
    /// Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? RankExact(
        IReadOnlyList<TidalPublicCatalog.TidalPublicTrack> tracks, TrackQuery query)
        => SearchRanker.RankExactRecording(
            query.Artist, query.Title, query.ExpectedDuration,
            tracks.Select(t => ToHit(t, query, exact: true)));

    /// <summary>The text route: the shared ranker's title/artist and marker rules. Internal for tests.</summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<TidalPublicCatalog.TidalPublicTrack> tracks, TrackQuery query)
    {
        var candidates = tracks
            .Where(t => t.StreamReady)
            .Select(t => ToHit(t, query))
            .ToList();
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, candidates);
    }

    private static DownloaderSearchHit ToHit(
        TidalPublicCatalog.TidalPublicTrack track, TrackQuery query, bool exact = false)
        => new(
            Title: track.Title,
            Artist: track.Artist,
            SourceUrl: track.SourceUrl,
            Duration: track.Duration,
            MatchConfidence: exact
                ? MatchConfidence.High
                : FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: track.Title));

    private async Task<string> CountryAsync(string downloaderId, CancellationToken ct)
    {
        try
        {
            var value = await _config.GetAsync(downloaderId, CountryKey, ct);
            return string.IsNullOrWhiteSpace(value) ? "US" : value.Trim();
        }
        catch
        {
            return "US";
        }
    }
}
