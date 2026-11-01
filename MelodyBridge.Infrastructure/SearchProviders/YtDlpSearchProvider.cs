using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches YouTube through yt-dlp's ytsearch5 extractor and ranks the
/// flat hits. YouTube does not index ISRCs, so this is the self-search
/// tier: it stays behind every official and community catalogue.
/// </summary>
public sealed class YtDlpSearchProvider : ISearchProvider
{
    public const string ProviderId = "youtube-search";
    public const string DownloaderId = "ytdlp";

    private readonly ILogger<YtDlpSearchProvider> _logger;
    private readonly Func<bool> _binaryAvailable;

    public YtDlpSearchProvider(ILogger<YtDlpSearchProvider> logger)
        : this(logger, () => YtDlpProcess.BinaryPath is not null)
    {
    }

    /// <summary>Test seam for the missing-binary branch; production reads PATH.</summary>
    internal YtDlpSearchProvider(
        ILogger<YtDlpSearchProvider> logger, Func<bool> binaryAvailable)
    {
        _logger = logger;
        _binaryAvailable = binaryAvailable;
    }

    public string Id => ProviderId;
    public string Name => "YouTube search";
    public string Description => "Searches YouTube through yt-dlp and ranks the top five results.";
    public SearchData Data => SearchData.Text;
    public SearchTier Tier => SearchTier.SelfSearch;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "YouTube search via yt-dlp",
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
        if (!_binaryAvailable()) return Array.Empty<DownloaderSearchHit>();

        // YouTube search is noisy enough already: dropping mix suffixes
        // and featuring tags keeps the real track on page one.
        var searchText = query.SearchText;
        if (searchText.Length == 0) return Array.Empty<DownloaderSearchHit>();
        return await SearchExtractorAsync(
            "ytsearch5", query.Artist, query.Title, searchText, ct);
    }

    private async Task<List<DownloaderSearchHit>> SearchExtractorAsync(
        string extractor, string artist, string title, string query, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
            {
                $"{extractor}:{query}",
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
            }, TimeSpan.FromSeconds(30), ct);

            if (exit != 0) return new List<DownloaderSearchHit>();
            return YtDlpSearchParser.ParseFlatPlaylistHits(stdout, artist, title,
                id => $"https://www.youtube.com/watch?v={id}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "yt-dlp search via {Extractor} failed for '{Query}'", extractor, query);
            return new List<DownloaderSearchHit>();
        }
    }
}
