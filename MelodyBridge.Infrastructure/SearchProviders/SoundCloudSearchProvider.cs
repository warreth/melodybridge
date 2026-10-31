using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches SoundCloud through yt-dlp's scsearch5 extractor. The original
/// upload is often the artist's own, but the first row is routinely a cover
/// or a repost, so the provider ranks the top five. No ISRC index: this is
/// the self-search tier.
/// </summary>
public sealed class SoundCloudSearchProvider : ISearchProvider
{
    public const string ProviderId = "soundcloud-search";
    public const string DownloaderId = "soundcloud";

    private readonly ILogger<SoundCloudSearchProvider> _logger;

    public SoundCloudSearchProvider(ILogger<SoundCloudSearchProvider> logger)
    {
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "SoundCloud search";
    public string Description => "Searches SoundCloud through yt-dlp and ranks the top five results.";
    public SearchData Data => SearchData.Text;
    public SearchTier Tier => SearchTier.SelfSearch;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "SoundCloud search via yt-dlp",
            Tier: SearchTier.SelfSearch) };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;
        var hits = await SearchAllAsync(downloaderId, query, ct);
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, hits);
    }

    public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<DownloaderSearchHit>();
        if (YtDlpProcess.BinaryPath is null) return Array.Empty<DownloaderSearchHit>();

        var searchText = query.SearchText;
        if (searchText.Length == 0) return Array.Empty<DownloaderSearchHit>();

        try
        {
            var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
            {
                $"scsearch5:{searchText}",
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
            }, TimeSpan.FromSeconds(30), ct);

            if (exit != 0) return Array.Empty<DownloaderSearchHit>();
            return YtDlpSearchParser.ParseFlatPlaylistHits(
                stdout, query.Artist, query.Title, id => id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoundCloud search failed for '{Query}'", searchText);
            return Array.Empty<DownloaderSearchHit>();
        }
    }
}
