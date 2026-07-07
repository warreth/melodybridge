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
    /// <summary>Write a structured log entry.</summary>
    void Log(LogLevel level, string category, string message, string? detail = null);

    /// <summary>Get a snapshot of all collected entries (newest first).</summary>
    IReadOnlyList<LogEntry> GetEntries();

    /// <summary>Clear all collected entries.</summary>
    void Clear();

    /// <summary>Maximum number of entries kept in memory (oldest dropped).</summary>
    int MaxEntries { get; }
}
