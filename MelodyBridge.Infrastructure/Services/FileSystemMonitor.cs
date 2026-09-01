using System.Collections.Concurrent;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Monitors library folders for file system changes using FileSystemWatcher.
/// Automatically triggers rescans when audio files are added, modified, or deleted.
/// </summary>
public class FileSystemMonitor : IFileSystemMonitor, IDisposable
{
    private readonly ILogger<FileSystemMonitor> _logger;
    private readonly ConcurrentDictionary<string, MonitorEntry> _monitors = new();
    private readonly ConcurrentDictionary<string, DateTime> _debounceCache = new();
    private readonly TimeSpan _debounceInterval = TimeSpan.FromSeconds(5);
    private readonly string[] _audioExtensions = { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".wav", ".webm" };

    public event EventHandler<FileSystemChangeEventArgs>? ChangeDetected;

    public FileSystemMonitor(ILogger<FileSystemMonitor> logger)
    {
        _logger = logger;
    }

    public void StartMonitoring(string path, int scanLocationId)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("Cannot monitor empty path");
            return;
        }

        if (!Directory.Exists(path))
        {
            _logger.LogWarning("Cannot monitor non-existent directory: {Path}", path);
            return;
        }

        if (_monitors.ContainsKey(path))
        {
            _logger.LogDebug("Already monitoring: {Path}", path);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            var entry = new MonitorEntry
            {
                Watcher = watcher,
                ScanLocationId = scanLocationId,
                Path = path
            };

            // Wire up events
            watcher.Created += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Created, entry);
            watcher.Deleted += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Deleted, entry);
            watcher.Changed += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Changed, entry);
            watcher.Renamed += (s, e) => OnFileSystemChanged(e.FullPath, FileSystemChangeType.Renamed, entry);
            watcher.Error += (s, e) => OnWatcherError(path, e);

            _monitors[path] = entry;
            _logger.LogInformation("Started monitoring: {Path} (ID: {ScanLocationId})", path, scanLocationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring: {Path}", path);
        }
    }

    public void StopMonitoring(string path)
    {
        if (_monitors.TryRemove(path, out var entry))
        {
            entry.Watcher.EnableRaisingEvents = false;
            entry.Watcher.Dispose();
            _logger.LogInformation("Stopped monitoring: {Path}", path);
        }
    }

    public void StopAll()
    {
        foreach (var kvp in _monitors)
        {
            try
            {
                kvp.Value.Watcher.EnableRaisingEvents = false;
                kvp.Value.Watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing watcher for {Path}", kvp.Key);
            }
        }
        _monitors.Clear();
        _debounceCache.Clear();
        _logger.LogInformation("Stopped all file system monitors");
    }

    public IReadOnlyList<string> GetMonitoredPaths()
    {
        return _monitors.Keys.ToList();
    }

    private void OnFileSystemChanged(string filePath, FileSystemChangeType changeType, MonitorEntry entry)
    {
        // Filter to audio files only
        var ext = Path.GetExtension(filePath);
        if (!_audioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return;

        // Debounce to prevent rapid-fire rescans
        var now = DateTime.UtcNow;
        var key = entry.Path;

        if (_debounceCache.TryGetValue(key, out var lastTrigger))
        {
            if (now - lastTrigger < _debounceInterval)
                return;
        }

        _debounceCache[key] = now;

        _logger.LogDebug("File system change detected: {ChangeType} - {File}", changeType, filePath);

        // Raise event
        try
        {
            ChangeDetected?.Invoke(this, new FileSystemChangeEventArgs(filePath, changeType, entry.ScanLocationId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ChangeDetected event handler");
        }
    }

    private void OnWatcherError(string path, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error for {Path}", path);
    }

    public void Dispose()
    {
        StopAll();
        GC.SuppressFinalize(this);
    }

    private class MonitorEntry
    {
        public FileSystemWatcher Watcher { get; set; } = null!;
        public int ScanLocationId { get; set; }
        public string Path { get; set; } = string.Empty;
    }
}
