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
}
