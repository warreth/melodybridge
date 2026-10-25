namespace MelodyBridge.Core.Logging;

/// <summary>
/// A single structured log entry collected by the application-wide <see cref="ILogCollector"/>.
/// The Sequence is the collector's stamp: two entries with identical
/// content in the same clock tick still differ, which is what page keys
/// and pin identity need (the record's value equality alone would
/// collapse them into one row).
/// </summary>
public record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Detail = null,
    string? Group = null,
    string Area = "System",
    long Sequence = 0
);

/// <summary>
/// Severity levels matching standard .NET <see cref="Microsoft.Extensions.Logging.LogLevel"/> numbering.
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Critical = 5,
}
