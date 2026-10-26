namespace MelodyBridge.Core.Logging;

/// <summary>
/// Application-wide log collector that aggregates structured log entries
/// from all components (providers, services, controllers). The live view
/// is bounded; the exporter also sees older warnings and errors kept in
/// a separate archive.
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

    /// <summary>Get a snapshot of the live entries (newest first).</summary>
    IReadOnlyList<LogEntry> GetEntries();

    /// <summary>
    /// Everything an export should carry: the live buffer plus warnings and
    /// errors that already rolled out of it, newest first and de-duplicated.
    /// </summary>
    IReadOnlyList<LogEntry> GetExportEntries();

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

    /// <summary>Maximum number of warning/error entries kept outside the live buffer.</summary>
    int MaxProblemEntries { get; }
}
