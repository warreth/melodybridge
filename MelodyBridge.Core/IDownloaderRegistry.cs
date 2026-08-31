namespace MelodyBridge.Core;

/// <summary>
/// Registry for downloader plugins: enumeration, enable/disable state
/// (persisted), and priority ordering for the download waterfall.
/// </summary>
public interface IDownloaderRegistry
{
    /// <summary>All registered downloader plugins.</summary>
    IReadOnlyList<IDownloader> GetAll();

    /// <summary>One downloader by ID.</summary>
    IDownloader? Get(string id);

    /// <summary>Enabled downloaders, ordered by configured priority.</summary>
    IReadOnlyList<IDownloader> GetEnabled();

    /// <summary>Enable or disable a downloader (persisted).</summary>
    Task SetEnabledAsync(string id, bool enabled);

    /// <summary>Whether a downloader is enabled.</summary>
    bool IsEnabled(string id);

    /// <summary>Configured priority (lower = tried earlier). Persisted.</summary>
    Task<int> GetPriorityAsync(string id, CancellationToken ct = default);

    /// <summary>Set the priority for a downloader. Persisted.</summary>
    Task SetPriorityAsync(string id, int priority, CancellationToken ct = default);

    /// <summary>
    /// Persist the full waterfall order in one call. The list is the exact
    /// desired order (index = priority, 0 first). Normalizes all priorities
    /// so they stay dense and comparable, which the swap-based setter cannot
    /// guarantee once values drift apart.
    /// </summary>
    Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);
}
