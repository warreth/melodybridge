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
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Qobuz ISRC-first search") };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = await _session.GetTokenAsync(ct);
        if (token is null) return null;

        // The Qobuz catalogue indexes ISRCs: searching the code itself
        // finds the exact recording even when the text search never
        // returns it (a soundtrack sharing words, a decorated title).
        // Only an exact ISRC match is accepted from that call; anything
        // else falls through to the text search.
        if (query.HasIsrc)
        {
            var byIsrc = await _qobuz.SearchTracksAsync(query.Isrc!, 5, token, ct);
            if (!_session.AcceptSearchResult(byIsrc)) return null;
            if (Rank(byIsrc.Tracks, query, requireIsrcMatch: true) is { } exact) return exact;
        }

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var result = await _qobuz.SearchTracksAsync(searchText, 10, token, ct);
        if (!_session.AcceptSearchResult(result)) return null;
        return Rank(result.Tracks, query);
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
