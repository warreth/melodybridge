using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches the Qobuz catalogue for the qobuz-dlp source. ISRC first when
/// the query carries one, then the cleaned text; the ranking is shared with
/// the other catalogue providers. The token and the auth verdict live in
/// <see cref="QobuzSession"/>, so the plugin card and the waterfall see the
/// same state.
/// </summary>
public sealed class QobuzSearchProvider : ISearchProvider
{
    public const string ProviderId = "qobuz-catalogue";
    public const string DownloaderId = QobuzSession.PluginId;
    public const string JumoDownloaderId = JumoSession.PluginId;
    public const string LucidaDownloaderId = "lucida";

    private readonly QobuzClient _qobuz;
    private readonly QobuzSession _session;

    public QobuzSearchProvider(QobuzClient qobuz, QobuzSession session)
    {
        _qobuz = qobuz;
        _session = session;
    }

    public string Id => ProviderId;
    public string Name => "Qobuz catalogue";
    public string Description => "Searches the Qobuz catalogue, ISRC first.";
    public SearchData Data => SearchData.Text | SearchData.Isrc;
    public int Priority => 30;

    public IReadOnlyList<SearchProviderTarget> Targets =>
    [
        new(DownloaderId, Data, "Qobuz ISRC-first search", Priority: 30),
        new(LucidaDownloaderId, Data,
            "Qobuz track URL for the Lucida ripper", Priority: 20, Tier: SearchTier.Ripper),
        new(JumoDownloaderId, Data,
            "Qobuz album and track ids mapped to a Jumo stream", Priority: 10,
            Tier: SearchTier.UnofficialCatalog),
    ];

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        var isJumo = string.Equals(downloaderId, JumoDownloaderId, StringComparison.OrdinalIgnoreCase);
        var isLucida = string.Equals(downloaderId, LucidaDownloaderId, StringComparison.OrdinalIgnoreCase);
        var isQobuz = string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase);
        if (!isQobuz && !isJumo && !isLucida) return null;

        var token = await _session.GetTokenAsync(ct);
        if (token is null) return null;

        DownloaderSearchHit? hit = null;
        IReadOnlyList<QobuzClient.QobuzTrack>? sourceTracks = null;

        // The Qobuz catalogue indexes ISRCs: searching the code itself
        // finds the exact recording even when the text search never
        // returns it (a soundtrack sharing words, a decorated title).
        // Only an exact ISRC match is accepted from that call; anything
        // else falls through to the text search.
        if (query.HasIsrc)
        {
            var byIsrc = await _qobuz.SearchTracksAsync(query.Isrc!, 5, token, ct);
            if (!_session.AcceptSearchResult(byIsrc)) return null;
            var exact = Rank(byIsrc.Tracks, query, requireIsrcMatch: true);
            if (exact is not null)
            {
                hit = exact;
                sourceTracks = byIsrc.Tracks;
            }
        }

        if (hit is null)
        {
            var searchText = query.SearchText;
            if (searchText.Length == 0) return null;

            var result = await _qobuz.SearchTracksAsync(searchText, 10, token, ct);
            if (!_session.AcceptSearchResult(result)) return null;
            hit = Rank(result.Tracks, query);
            sourceTracks = result.Tracks;
        }

        if (hit is null) return null;
        return isJumo ? MapToJumo(sourceTracks, hit) : hit;
    }

    /// <summary>
    /// Jumo is a Qobuz front-end and keys its stream by the Qobuz album
    /// id and track id. The official hit becomes a <c>jumo:</c> URL
    /// without a second catalogue search; a missing album id is a miss
    /// and leaves the Jumo provider as the fallback.
    /// </summary>
    private static DownloaderSearchHit? MapToJumo(
        IReadOnlyList<QobuzClient.QobuzTrack>? tracks, DownloaderSearchHit hit)
    {
        if (tracks is null || hit.SourceUrl is not { Length: > 0 } url) return null;
        var last = url[(url.LastIndexOf('/') + 1)..];
        if (!long.TryParse(last, out var trackId)) return null;
        var track = tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track?.AlbumId is not { Length: > 0 } albumId) return null;
        return hit with { SourceUrl = $"jumo:{albumId}:{track.Id}" };
    }

    /// <summary>
    /// Ranks the Qobuz results with the shared rules. An exact ISRC narrows
    /// the pool to the recording itself and is accepted on that code (the
    /// title spacing differs across catalogues), with the length still
    /// checked. Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<QobuzClient.QobuzTrack> tracks, TrackQuery query,
        bool requireIsrcMatch = false)
    {
        var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
        foreach (var track in tracks)
        {
            var hit = new DownloaderSearchHit(
                Title: track.Title,
                Artist: track.Artist,
                SourceUrl: $"https://play.qobuz.com/track/{track.Id}",
                Duration: track.Duration,
                MatchConfidence: FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: track.Title),
                // The catalog already knows this track's ceiling. A
                // CD-only track reports 44.1 kHz even when the configured
                // tier allows hi-res, so the picker shows the real file
                // instead of the tier. When hi-res is not streamable,
                // CD quality is the ceiling whatever the master holds.
                BitDepth: track.HiresStreamable ? track.MaximumBitDepth : 16,
                SampleRateHz: track.HiresStreamable ? track.MaximumSamplingRate : 44100);
            candidates.Add((hit, track.Isrc));
        }

        if (candidates.Count == 0) return null;

        var isrcPool = candidates.Where(c =>
            !string.IsNullOrWhiteSpace(query.Isrc) &&
            !string.IsNullOrWhiteSpace(c.Isrc) &&
            string.Equals(query.Isrc.Trim(), c.Isrc.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (isrcPool.Count > 0)
            return SearchRanker.RankExactRecording(query.Artist, query.Title,
                query.ExpectedDuration, isrcPool.Select(c => c.Hit));
        if (requireIsrcMatch) return null;
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration,
            candidates.Select(c => c.Hit));
    }
}
