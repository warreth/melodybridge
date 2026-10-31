using MelodyBridge.Core;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// The title-only Kuwo/NetEase search. It has no ISRC index and stays the
/// self-search tier, last after every catalogue. The provider and the
/// downloader share a session for the base URL, platform setting and the
/// negative availability reason.
/// </summary>
public sealed class FlacMusicSearchProvider : ISearchProvider
{
    public const string ProviderId = "flacmusic-search";
    public const string DownloaderId = "flacmusic";

    private readonly FlacMusic.FlacMusicSession _session;
    private readonly ILogger<FlacMusicSearchProvider> _logger;

    public FlacMusicSearchProvider(
        FlacMusic.FlacMusicSession session, ILogger<FlacMusicSearchProvider> logger)
    {
        _session = session;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "FLAC Music search";
    public string Description => "Title-only Kuwo and NetEase search; no ISRC index.";
    public SearchData Data => SearchData.Text;
    public SearchTier Tier => SearchTier.SelfSearch;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Title-only Kuwo and NetEase search",
            Tier: SearchTier.SelfSearch) };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;
        var candidates = await BuildCandidatesAsync(query, ct);
        if (candidates.Count == 0) return null;
        return SearchRanker.Rank(
            query.Artist, query.Title, query.ExpectedDuration, candidates,
            maxAlternatives: 3,
            tieBreaker: FlacMusic.FlacMusicSource.QualityRank);
    }

    public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<DownloaderSearchHit>();
        return (await BuildCandidatesAsync(query, ct))
            .OrderByDescending(c => FuzzyMatcher.ScoreWithDuration(
                query.Artist, query.Title, query.ExpectedDuration, c.Artist, c.Title, c.Duration))
            .ThenByDescending(FlacMusic.FlacMusicSource.QualityRank)
            .Take(10)
            .ToList();
    }

    private async Task<List<DownloaderSearchHit>> BuildCandidatesAsync(
        TrackQuery query, CancellationToken ct)
    {
        var keyword = query.SearchText;
        if (keyword.Length == 0) return new List<DownloaderSearchHit>();

        await _session.BaseUrlAsync(ct);
        var platforms = await _session.PlatformsAsync(ct);
        var results = await Task.WhenAll(platforms.Select(platform =>
            _session.Client.SearchAsync(platform, keyword, size: 20, ct)));

        var failed = results.Where(r => r.Status != FlacMusicClient.Status.Ok).ToList();
        if (failed.Count == results.Length)
        {
            var reason = failed.Select(f => f.Message).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            _session.SetUnavailable(reason ?? "flac.music.hi.cn could not be searched");
            _logger.LogDebug("FLAC Music search failed: {Reason}", _session.LastUnavailableReason);
            return new List<DownloaderSearchHit>();
        }

        var hits = new List<DownloaderSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in results.SelectMany(r => r.Tracks))
        {
            if (!seen.Add($"{track.Platform}:{track.Id}")) continue;
            var format = FlacMusic.FlacMusicSource.SelectFormat(track.Formats, query.Quality);
            if (format is null) continue;

            var title = FlacMusic.FlacMusicSource.CleanTitle(track.Title);
            var hit = new DownloaderSearchHit(
                Title: title,
                Artist: track.Artist,
                SourceUrl: FlacMusic.FlacMusicSource.EncodeSourceUrl(track, title, query.Quality),
                Duration: track.Duration,
                BitrateKbps: format.IsFlac ? null : format.BitrateKbps,
                MatchConfidence: FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: title));
            hits.Add(hit);
        }
        return hits;
    }
}
