using MelodyBridge.Core;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Model for a single dev-panel log entry.
/// </summary>
public record DevLogEntry(
    DateTime Timestamp,
    string Level,
    string Category,
    string Message,
    string? Detail = null
);

/// <summary>
/// A search result enriched with the provider that found it, for interactive display.
/// </summary>
public record InteractiveSearchResult(
    string Title,
    string Artist,
    string? Album,
    string Url,
    Platform SourcePlatform,
    IReadOnlyList<TrackQuality> AvailableQualities,
    string ProviderId,
    string ProviderName
);

/// <summary>
/// Tracks the state of a single download in the dev panel.
/// </summary>
public class DevDownloadTask
{
    public string Id { get; set; } = "";
    public string TrackInfo { get; set; } = "";
    public string Url { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? ResultPath { get; set; }
    public string? ErrorMessage { get; set; }

    public DevDownloadTask() { }

    public DevDownloadTask(string id, string trackInfo, string url, string providerName,
        string status, string? resultPath, string? errorMessage)
    {
        Id = id;
        TrackInfo = trackInfo;
        Url = url;
        ProviderName = providerName;
        Status = status;
        ResultPath = resultPath;
        ErrorMessage = errorMessage;
    }
}

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

    // ── Search state ──
    public string? SearchQuery { get; set; }
    public string? SearchProviderId { get; set; }
    public string? LastSearchResult { get; set; }
    public List<InteractiveSearchResult> SearchResults { get; set; } = new();

    // ── Download state (single direct download) ──
    public string? DownloadUrl { get; set; }
    public string? SelectedProviderId { get; set; }
    public string? SelectedQuality { get; set; }
    public string? LastDownloadResult { get; set; }

    // ── Download queue ──
    public List<DevDownloadTask> DownloadQueue { get; set; } = new();
    private int _downloadTaskCounter;

    /// <summary>Allocate a new download task ID.</summary>
    public string NextDownloadTaskId() =>
        $"dev-{Interlocked.Increment(ref _downloadTaskCounter)}";

    // ── Logging ──

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
