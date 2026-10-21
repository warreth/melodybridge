namespace MelodyBridge.Application.Services;

/// <summary>
/// The download counters on the playlists page, and the store stamp they
/// were read at.
/// <para>
/// The stamp is read before the counters. A write that lands while they
/// are being read moves the stamp past the one that was read, so the
/// next tick reloads. Reading the stamp afterwards would record a write
/// the counters never saw, and the page would sit on stale numbers
/// until something else wrote to the store.
/// </para>
/// </summary>
public sealed class DownloadStatsRefresher(
    Func<Task<Dictionary<string, (int downloaded, int failed, int total, long sizeBytes, int attention)>>> read,
    Func<long> version)
{
    /// <summary>The counters as they were last read.</summary>
    public Dictionary<string, (int downloaded, int failed, int total, long sizeBytes, int attention)> Stats { get; private set; } = new();

    /// <summary>The store stamp the counters were read at.</summary>
    public long ObservedVersion { get; private set; }

    /// <summary>
    /// True when the counters must be read again: a run is live, or the
    /// store moved since the last read.
    /// </summary>
    public bool ShouldReload(bool active) => active || version() != ObservedVersion;

    /// <summary>
    /// Reads the counters and remembers the stamp they were read at.
    /// </summary>
    public async Task RefreshAsync()
    {
        var seen = version();
        Stats = await read();
        ObservedVersion = seen;
    }
}
