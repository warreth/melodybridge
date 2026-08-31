namespace MelodyBridge.Core;

/// <summary>Severity of an in-app log event.</summary>
public enum AppLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>One in-app log event, as shown on the Logs page.</summary>
public record AppLogEntry(
    DateTime TimestampUtc,
    AppLogLevel Level,
    string Category,
    string Message);

/// <summary>
/// In-memory recent-event store for the Logs page. Implementations keep a
/// bounded ring buffer of the events that matter for operating the app:
/// playlist syncs, downloads, scans, plugin state, errors.
/// </summary>
public interface IAppLogCollector
{
    /// <summary>Add one event. Timestamps are UTC.</summary>
    void Add(AppLogLevel level, string category, string message);

    /// <summary>Recent events, oldest first. Bounded by the implementation.</summary>
    IReadOnlyList<AppLogEntry> Snapshot();

    /// <summary>Remove all buffered events.</summary>
    void Clear();
}
