using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches the public Deezer catalogue for the deemix source. Deezer's
/// advanced search indexes ISRCs (isrc:"..."), so the code runs first when
/// the query has one; the text search is the fallback. The ranking is the
/// same shared rule the other catalogue providers use.
/// </summary>
public sealed class DeemixSearchProvider : ISearchProvider
{
    public const string ProviderId = "deezer-catalogue";
    public const string DownloaderId = "deemix";

    private readonly DeezerClient _deezer;

    public DeemixSearchProvider(DeezerClient deezer)
    {
        _deezer = deezer;
    }

    public string Id => ProviderId;
    public string Name => "Deezer catalogue";
    public string Description => "Searches the Deezer catalogue, ISRC first.";
    public SearchData Data => SearchData.Text | SearchData.Isrc;
    public int Priority => 10;

    public IReadOnlyList<SearchProviderTarget> Targets =>
    [
        new(DownloaderId, Data, "Deezer ISRC-first search", Priority: 30),
        new("lucida", Data, "Deezer track URL for the Lucida ripper",
            Priority: 20, Tier: SearchTier.Ripper),
    ];

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        var isLucida = string.Equals(downloaderId, "lucida", StringComparison.OrdinalIgnoreCase);
        if (!isLucida
            && !string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        // Deezer's advanced search indexes ISRCs (isrc:"..."): the code
        // finds the exact recording even when the text search does not
        // return it. Only an exact match from that call is accepted.
        if (query.HasIsrc)
        {
            var byIsrc = await _deezer.SearchAsync($"isrc:\"{query.Isrc!}\"", 5, ct);
            if (Rank(byIsrc, query, requireIsrcMatch: true) is { } exact) return exact;
        }

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var tracks = await _deezer.SearchAsync(searchText, 10, ct);
        return Rank(tracks, query);
    }

    /// <summary>
    /// Ranks the public Deezer results with the shared rules. An exact
    /// ISRC narrows the pool to the recording itself and is accepted on
    /// that code (the title wording differs across catalogues), with the
    /// length still checked. Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<DeezerClient.DeezerTrack> tracks, TrackQuery query,
        bool requireIsrcMatch = false)
    {
        var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
        foreach (var track in tracks)
        {
            var hit = new DownloaderSearchHit(
                Title: track.Title,
                Artist: track.Artist,
                SourceUrl: $"https://www.deezer.com/track/{track.Id}",
                Duration: track.Duration,
                MatchConfidence: FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: track.Title));
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
