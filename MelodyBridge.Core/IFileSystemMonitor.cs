namespace MelodyBridge.Core;

/// <summary>
/// Service for monitoring file system changes in library folders.
/// Triggers automatic rescans when files are added, modified, or deleted.
/// </summary>
public interface IFileSystemMonitor
{
    /// <summary>
    /// Start monitoring a directory for changes.
    /// </summary>
    void StartMonitoring(string path, int scanLocationId);

    /// <summary>
    /// Stop monitoring a directory.
    /// </summary>
    void StopMonitoring(string path);

    /// <summary>
    /// Stop all active monitors.
    /// </summary>
    void StopAll();

    /// <summary>
    /// Get list of currently monitored paths.
    /// </summary>
    IReadOnlyList<string> GetMonitoredPaths();

    /// <summary>
    /// Event raised when a file system change triggers a rescan.
    /// </summary>
    event EventHandler<FileSystemChangeEventArgs>? ChangeDetected;
}

/// <summary>
/// Event arguments for file system change notifications.
/// </summary>
public class FileSystemChangeEventArgs : EventArgs
{
    public string Path { get; }
    public FileSystemChangeType ChangeType { get; }
    public int ScanLocationId { get; }
    public DateTime Timestamp { get; }

    public FileSystemChangeEventArgs(string path, FileSystemChangeType changeType, int scanLocationId)
    {
        Path = path;
        ChangeType = changeType;
        ScanLocationId = scanLocationId;
        Timestamp = DateTime.UtcNow;
    }
}

/// <summary>
/// Type of file system change detected.
/// </summary>
public enum FileSystemChangeType
{
    Created,
    Deleted,
    Changed,
    Renamed
}
