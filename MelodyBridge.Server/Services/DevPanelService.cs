using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;

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
///
/// Logging is delegated to the application-wide <see cref="ILogCollector"/>
/// so that all components (providers, services, controllers) share the same
/// unified log buffer visible in the DevPanel.
/// </summary>
public class DevPanelService
{
    private readonly ILogCollector _logCollector;
    private static readonly Dictionary<string, CoreLogLevel> StringToLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Trace"] = CoreLogLevel.Trace,
        ["Debug"] = CoreLogLevel.Debug,
        ["Info"] = CoreLogLevel.Info,
        ["Warn"] = CoreLogLevel.Warn,
        ["Error"] = CoreLogLevel.Error,
        ["Critical"] = CoreLogLevel.Critical,
    };

    private static readonly Dictionary<CoreLogLevel, string> LevelToString = new()
    {
        [CoreLogLevel.Trace] = "Trace",
        [CoreLogLevel.Debug] = "Debug",
        [CoreLogLevel.Info] = "Info",
        [CoreLogLevel.Warn] = "Warn",
        [CoreLogLevel.Error] = "Error",
        [CoreLogLevel.Critical] = "Critical",
    };

    public DevPanelService() : this(new LogCollector()) { }

    public DevPanelService(ILogCollector logCollector)
    {
        _logCollector = logCollector;
    }

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

    // ── Logging (delegated to ILogCollector) ──

    public void Log(string level, string category, string message, string? detail = null)
    {
        var mapped = StringToLevel.TryGetValue(level, out var l) ? l : CoreLogLevel.Info;
        _logCollector.Log(mapped, category, message, detail);
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
        return _logCollector.GetEntries()
            .Select(e => new DevLogEntry(
                e.Timestamp,
                LevelToString.TryGetValue(e.Level, out var s) ? s : e.Level.ToString(),
                e.Category,
                e.Message,
                e.Detail))
            .ToList();
    }

    /// <summary>Clear all logs.</summary>
    public void ClearLogs() => _logCollector.Clear();

    /// <summary>The underlying ILogCollector, for direct access (export, etc.).</summary>
    public ILogCollector LogCollector => _logCollector;
}
