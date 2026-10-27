using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Routes each source's search through the registered providers, and
/// through the source's own search only when the source declares that it
/// can search itself. This is the seam that lets a source be a pure URL
/// downloader: the Tidal searcher resolves the URL, the Rhythmax and tiddl
/// sources only download it.
///
/// A provider failure is a miss, not a crash: the waterfall must still be
/// able to try the next source, and the manual picker must still show the
/// sources that did answer.
/// </summary>
public sealed class SearchProviderRegistry : ISearchProviderRegistry
{
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly ILogger<SearchProviderRegistry> _logger;

    public SearchProviderRegistry(
        IEnumerable<ISearchProvider> providers,
        ILogger<SearchProviderRegistry> logger)
    {
        _providers = providers
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _logger = logger;
    }

    public IReadOnlyList<ISearchProvider> GetAll() => _providers;

    public ISearchProvider? ForDownloader(string downloaderId)
        => _providers.FirstOrDefault(p => p.Targets.Any(t =>
            string.Equals(t.DownloaderId, downloaderId, StringComparison.OrdinalIgnoreCase)));

    public async Task<DownloaderSearchHit?> SearchAsync(
        IDownloader downloader, TrackQuery query, CancellationToken ct = default)
    {
        var info = downloader.SearchInfo;
        switch (info.Mode)
        {
            case DownloaderSearchMode.None:
                return null;

            case DownloaderSearchMode.Self:
                return await downloader.SearchAsync(query, ct);

            case DownloaderSearchMode.Provider:
            {
                var provider = ForDownloader(downloader.Id);
                if (provider is null)
                {
                    _logger.LogWarning(
                        "{Source} declares provider search but no search provider targets it",
                        downloader.Name);
                    return null;
                }

                try
                {
                    var hit = await provider.SearchAsync(downloader.Id, query, ct);
                    _logger.LogDebug(
                        "{Provider} searched {Source} for '{Artist} - {Title}': {Result}",
                        provider.Name, downloader.Name, query.Artist, query.Title,
                        hit is null ? "no match" : hit.Title);
                    return hit;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Provider} search for {Source} failed",
                        provider.Name, downloader.Name);
                    return null;
                }
            }

            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        IDownloader downloader, TrackQuery query, CancellationToken ct = default)
    {
        var info = downloader.SearchInfo;
        if (info.Mode == DownloaderSearchMode.None) return Array.Empty<DownloaderSearchHit>();
        if (info.Mode == DownloaderSearchMode.Self)
            return await downloader.SearchAllAsync(query, ct);

        var providers = _providers
            .Where(p => p.Targets.Any(t =>
                string.Equals(t.DownloaderId, downloader.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (providers.Count == 0)
        {
            _logger.LogWarning(
                "{Source} declares provider search but no search provider targets it",
                downloader.Name);
            return Array.Empty<DownloaderSearchHit>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<DownloaderSearchHit>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hit = await provider.SearchAsync(downloader.Id, query, ct);
                if (hit is null) continue;
                Add(hit);
                if (hit.Alternatives is { Count: > 0 } alternatives)
                    foreach (var alternative in alternatives) Add(alternative);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Provider} search for {Source} failed",
                    provider.Name, downloader.Name);
            }
        }
        return hits;

        void Add(DownloaderSearchHit hit)
        {
            if (string.IsNullOrWhiteSpace(hit.SourceUrl) || !seen.Add(hit.SourceUrl)) return;
            hits.Add(hit);
        }
    }
}
