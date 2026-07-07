namespace MelodyBridge.Core.Logging;

/// <summary>
/// A single structured log entry collected by the application-wide <see cref="ILogCollector"/>.
/// </summary>
public record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Detail = null
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
