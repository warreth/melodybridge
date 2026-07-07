namespace MelodyBridge.Server.Services;

/// <summary>
/// Model for a single dev-panel log entry.
/// </summary>
public record DevLogEntry(
    DateTime Timestamp,
    string Level,    // Info, Warn, Error, Debug
    string Category, // e.g. Download, Search, Provider
    string Message,
    string? Detail = null
);

/// <summary>
/// Singleton service that tracks dev-panel state and accumulates
/// log entries for in-browser inspection.
/// </summary>
public class DevPanelService
{
    private readonly List<DevLogEntry> _logs = new();
    private readonly object _lock = new();

    /// <summary>Whether the dev panel sidebar link is visible.</summary>
    public bool Enabled { get; set; }

    /// <summary>Current page being tested in the Download Tester section.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Selected provider ID for the Download Tester.</summary>
    public string? SelectedProviderId { get; set; }

    /// <summary>Selected quality for the Download Tester.</summary>
    public string? SelectedQuality { get; set; }

    /// <summary>Query text for the Search Tester section.</summary>
    public string? SearchQuery { get; set; }

    /// <summary>Selected provider ID for the Search Tester.</summary>
    public string? SearchProviderId { get; set; }

    /// <summary>Last download result path or error.</summary>
    public string? LastDownloadResult { get; set; }

    /// <summary>Last search result summary.</summary>
    public string? LastSearchResult { get; set; }

    /// <summary>Append a log entry.</summary>
    public void Log(string level, string category, string message, string? detail = null)
    {
        var entry = new DevLogEntry(DateTime.UtcNow, level, category, message, detail);
        lock (_lock)
        {
            _logs.Add(entry);
            if (_logs.Count > 500)
                _logs.RemoveRange(0, _logs.Count - 500);
        }
    }

    public void LogInfo(string category, string message, string? detail = null)
        => Log("Info", category, message, detail);

    public void LogWarn(string category, string message, string? detail = null)
        => Log("Warn", category, message, detail);

    public void LogError(string category, string message, string? detail = null)
        => Log("Error", category, message, detail);

    public void LogDebug(string category, string message, string? detail = null)
        => Log("Debug", category, message, detail);

    /// <summary>Get a snapshot of all logs (newest first).</summary>
    public IReadOnlyList<DevLogEntry> GetLogs()
    {
        lock (_lock)
            return _logs.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>Clear all logs.</summary>
    public void ClearLogs()
    {
        lock (_lock) _logs.Clear();
    }
}
