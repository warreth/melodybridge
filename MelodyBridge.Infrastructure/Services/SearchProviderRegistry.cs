using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
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
    private readonly IReadOnlyList<string> _issues;
    private readonly ILogger<SearchProviderRegistry> _logger;

    public SearchProviderRegistry(
        IEnumerable<ISearchProvider> providers,
        ILogger<SearchProviderRegistry> logger,
        IDownloaderRegistry? downloaders = null)
    {
        _logger = logger;

        var all = providers.ToList();

        // Bridge the sources whose search still lives in the downloader.
        // A source that already has an explicit provider target keeps it.
        if (downloaders is not null)
        {
            foreach (var downloader in downloaders.GetAll())
            {
                if (downloader.SearchInfo.Mode != DownloaderSearchMode.Provider) continue;
                if (all.Any(provider => provider.Targets.Any(target =>
                        string.Equals(target.DownloaderId, downloader.Id,
                            StringComparison.OrdinalIgnoreCase))))
                    continue;
                all.Add(new LegacyDownloaderSearchProvider(downloader));
                _logger.LogDebug(
                    "Using the legacy search adapter for {Source}; no standalone provider targets it",
                    downloader.Name);
            }
        }

        var duplicateIds = all
            .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicateIds.Count > 0)
            throw new InvalidOperationException(
                "Duplicate search provider ids: " + string.Join(", ", duplicateIds));

        _providers = all
            .OrderByDescending(provider => provider.Priority)
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issues = new List<string>();
        foreach (var provider in _providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id))
                throw new InvalidOperationException("A search provider has no id");

            var duplicateTargets = provider.Targets
                .GroupBy(target => target.DownloaderId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateTargets.Count > 0)
                throw new InvalidOperationException(
                    $"Search provider {provider.Id} targets the same source twice: {string.Join(", ", duplicateTargets)}");

            if (provider.Targets.Count == 0)
                issues.Add($"{provider.Name} has no target source.");

            foreach (var target in provider.Targets)
            {
                if (string.IsNullOrWhiteSpace(target.DownloaderId))
                {
                    issues.Add($"{provider.Name} has a target with no source id.");
                    continue;
                }

                if (downloaders is not null && downloaders.Get(target.DownloaderId) is null)
                {
                    var issue = $"{provider.Name} targets {target.DownloaderId}, which is not a registered source.";
                    issues.Add(issue);
                    _logger.LogError("Search provider {Provider}: {Issue}", provider.Id, issue);
                }
            }
        }

        _issues = issues;
    }

    public IReadOnlyList<ISearchProvider> GetAll() => _providers;

    public IReadOnlyList<string> ValidationIssues => _issues;

    public ISearchProvider? ForDownloader(string downloaderId)
        => ProvidersFor(downloaderId).FirstOrDefault();

    public IReadOnlyDictionary<string, int> ProviderFirstOrder()
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var cell in _providers
                     .SelectMany(provider => provider.Targets
                         .Select(target => (Provider: provider, Target: target)))
                     .OrderBy(cell => cell.Target.Tier)
                     .ThenByDescending(cell => cell.Provider.Priority)
                     .ThenByDescending(cell => cell.Target.Priority)
                     .ThenBy(cell => cell.Provider.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(cell => cell.Target.DownloaderId, StringComparer.OrdinalIgnoreCase))
        {
            if (order.ContainsKey(cell.Target.DownloaderId)) continue;
            order[cell.Target.DownloaderId] = index++;
        }
        return order;
    }

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
                var providers = ProvidersFor(downloader.Id).ToList();
                if (providers.Count == 0)
                {
                    _logger.LogWarning(
                        "{Source} declares provider search but no search provider targets it",
                        downloader.Name);
                    return null;
                }

                // The best provider may be configured but unable to answer
                // (a missing Qobuz token, a Tidal rate limit). The next
                // provider for the same source is the documented fallback,
                // so the waterfall must not stop at the first miss.
                foreach (var provider in providers)
                {
                    try
                    {
                        var hit = await provider.SearchAsync(downloader.Id, query, ct);
                        _logger.LogDebug(
                            "{Provider} searched {Source} for '{Artist} - {Title}': {Result}",
                            provider.Name, downloader.Name, query.Artist, query.Title,
                            hit is null ? "no match" : hit.Title);
                        if (hit is not null) return hit;
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
                return null;
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

        var providers = ProvidersFor(downloader.Id).ToList();
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

    /// <summary>
    /// Providers that can serve the source, best first. The target's own
    /// priority is the most specific signal, then the provider priority,
    /// then the name for a stable tie.
    /// </summary>
    private IEnumerable<ISearchProvider> ProvidersFor(string downloaderId)
        => _providers
            .Select(provider => (Provider: provider, Target: provider.Targets
                .Where(target => string.Equals(target.DownloaderId, downloaderId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(target => target.Priority)
                .FirstOrDefault()))
            .Where(entry => entry.Target is not null)
            .OrderByDescending(entry => entry.Target!.Priority)
            .ThenByDescending(entry => entry.Provider.Priority)
            .ThenBy(entry => entry.Provider.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Provider);
}
