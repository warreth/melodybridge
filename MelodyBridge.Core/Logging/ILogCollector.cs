namespace MelodyBridge.Core.Logging;

/// <summary>
/// Application-wide log collector that aggregates structured log entries
/// from all components (providers, services, controllers, DevPanel).
///
/// Provides a uniform view of all recent log activity, consumed by
/// the DevPanel log viewer and the log exporter.
/// </summary>
public interface ILogCollector
{
    /// <summary>
    /// Write a structured log entry. When <paramref name="area"/> is omitted
    /// the collector derives it from the category once, at capture time, so
    /// every stored entry carries the area the Logs page filters on.
    /// </summary>
    void Log(LogLevel level, string category, string message,
        string? detail = null, string? group = null, string? area = null);

    /// <summary>Get a snapshot of all collected entries (newest first).</summary>
    IReadOnlyList<LogEntry> GetEntries();

    /// <summary>
    /// A monotonic count of everything ever collected (including evicted
    /// entries): the cheap idle signal a polling surface compares instead
    /// of re-reading the whole buffer each tick.
    /// </summary>
    long TotalCollected { get; }

    /// <summary>Clear all collected entries.</summary>
    void Clear();

    /// <summary>
    /// Changes whenever the buffer changes (new entry or clear), so pages
    /// can skip work while nothing happened.
    /// </summary>
    long Version { get; }

    /// <summary>Maximum number of entries kept in memory (oldest dropped).</summary>
    int MaxEntries { get; }
}
