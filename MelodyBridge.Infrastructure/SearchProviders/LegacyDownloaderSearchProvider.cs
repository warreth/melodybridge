using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Adapter for a source whose catalogue search still lives inside the
/// downloader. The registry creates one of these for every source that
/// declares provider search but has no standalone provider yet, so the
/// waterfall and the Plugins page see a provider route while the old
/// source-specific code stays untouched in the legacy plugin.
///
/// This is deliberately a bridge, not the end state: extracting a
/// catalogue search into its own provider class removes the adapter for
/// that source without changing the router or the UI.
/// </summary>
public sealed class LegacyDownloaderSearchProvider : ISearchProvider
{
    private readonly IDownloader _downloader;

    public LegacyDownloaderSearchProvider(IDownloader downloader)
    {
        _downloader = downloader;
    }

    public string Id => "legacy:" + _downloader.Id;
    public string Name => _downloader.Name + " catalogue";
    public string Description =>
        "Searches the " + _downloader.Name + " catalogue"
        + (Data.HasFlag(SearchData.Isrc) ? ", ISRC first." : ".");
    public SearchData Data => _downloader.SearchInfo.Data;
    public bool IsLegacy => true;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(_downloader.Id, Data, "Legacy source-side search") };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, _downloader.Id, StringComparison.OrdinalIgnoreCase))
            return null;
        return await _downloader.SearchAsync(query, ct);
    }
}
