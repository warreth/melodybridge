namespace MelodyBridge.Core;

/// <summary>
/// The query data a source or a search provider can work with. Flags so a
/// provider can say "ISRC first, title as the fallback" without inventing a
/// new enum value for every combination the UI has to explain.
/// </summary>
[Flags]
public enum SearchData
{
    None = 0,
    Title = 1,
    Artist = 2,
    /// <summary>The artist and title text every catalogue search accepts.</summary>
    Text = Title | Artist,
    Isrc = 4,
    /// <summary>A direct source URL the downloader accepts.</summary>
    Url = 8,
    Duration = 16,
}

/// <summary>How a download source gets its candidate URLs.</summary>
public enum DownloaderSearchMode
{
    /// <summary>The source only accepts direct URLs; nothing can search for it.</summary>
    None,

    /// <summary>The source searches its own catalogue (title, ISRC or both).</summary>
    Self,

    /// <summary>A registered search provider finds the URLs; the source only downloads.</summary>
    Provider,
}

/// <summary>
/// One source's search story, shown on the Plugins page and used by the
/// search router. <paramref name="Data"/> is what can be matched: for a
/// self-searching source that is what its own search reads, for a
/// provider-backed source it is what a provider can resolve, and for a
/// URL-only source it is <see cref="SearchData.Url"/>.
/// </summary>
public sealed record DownloaderSearchInfo(
    DownloaderSearchMode Mode,
    SearchData Data,
    string? Note = null)
{
    /// <summary>The source searches its own catalogue with this data.</summary>
    public static DownloaderSearchInfo Self(SearchData data, string? note = null)
        => new(DownloaderSearchMode.Self, data, note);

    /// <summary>A search provider supplies URLs for this source.</summary>
    public static DownloaderSearchInfo ViaProvider(SearchData data, string? note = null)
        => new(DownloaderSearchMode.Provider, data, note);

    /// <summary>Direct URLs only; no search is possible.</summary>
    public static DownloaderSearchInfo UrlOnly(string? note = null)
        => new(DownloaderSearchMode.None, SearchData.Url, note);
}

/// <summary>
/// One source a search provider can resolve, and the query data that
/// provider uses for it. The same provider can serve several downloaders
/// with different limits (for example Tidal's ISRC catalogue serves both
/// the tiddl source and the Rhythmax source).
/// </summary>
public sealed record SearchProviderTarget(
    string DownloaderId,
    SearchData Data,
    string? Note = null);

/// <summary>
/// A standalone catalogue/go-to-source search. A provider resolves a track
/// query into one or more download URLs; the downloader itself never has to
/// know how the URL was found. This is what lets a source be URL+ISRC-only
/// while text search and ISRC lookup live in one place.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable id, used by the Plugins page and logs.</summary>
    string Id { get; }

    /// <summary>Human name, for example "Tidal ISRC search".</summary>
    string Name { get; }

    /// <summary>What this provider does and where it gets its answers.</summary>
    string Description { get; }

    /// <summary>The query data the provider can consume.</summary>
    SearchData Data { get; }

    /// <summary>The download sources this provider can supply.</summary>
    IReadOnlyList<SearchProviderTarget> Targets { get; }

    /// <summary>
    /// Resolve one query for one target source. Null means "no match";
    /// transport and authentication failures are the provider's own
    /// business and must not throw past the router.
    /// </summary>
    Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default);
}

/// <summary>
/// The app-wide search router: providers first, a source's own search only
/// where that source explicitly declares one. The waterfall and the manual
/// picker both go through here so a "URL and ISRC only" source behaves the
/// same in both.
/// </summary>
public interface ISearchProviderRegistry
{
    /// <summary>All registered providers, for the Plugins page.</summary>
    IReadOnlyList<ISearchProvider> GetAll();

    /// <summary>The first provider that can supply this source, or null.</summary>
    ISearchProvider? ForDownloader(string downloaderId);

    /// <summary>The best hit for a source, or null when no provider and no self-search.</summary>
    Task<DownloaderSearchHit?> SearchAsync(
        IDownloader downloader, TrackQuery query, CancellationToken ct = default);

    /// <summary>
    /// Every hit a source can offer, including runner-ups, for the manual
    /// source picker. A provider-backed source never falls back to its own
    /// search here, even if it still carries one for compatibility.
    /// </summary>
    Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        IDownloader downloader, TrackQuery query, CancellationToken ct = default);
}
