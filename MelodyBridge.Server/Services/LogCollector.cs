using MelodyBridge.Core.Logging;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Singleton in-memory log collector. Thread-safe, bounded at <see cref="MaxEntries"/>.
/// All components (providers, services, controllers, DevPanel) feed into this,
/// giving the DevPanel and exporter a unified view.
/// </summary>
public sealed class LogCollector : ILogCollector
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();
    private int _nextId;

    /// <summary>
    /// Maximum number of entries retained in memory. Default 1000.
    /// </summary>
    public int MaxEntries { get; }

    public LogCollector(int maxEntries = 1000)
    {
        MaxEntries = maxEntries;
    }

    public void Log(CoreLogLevel level, string category, string message, string? detail = null)
    {
        var entry = new LogEntry(
            DateTime.UtcNow,
            level,
            category,
            message,
            detail
        );

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            Interlocked.Increment(ref _nextId);
        }
    }

    public IReadOnlyList<LogEntry> GetEntries()
    {
        lock (_lock)
            return _entries.OrderByDescending(e => e.Timestamp).ToList();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    /// <summary>
    /// Total entries collected since startup (including evicted ones).
    /// </summary>
    public long TotalCollected => _nextId;
}
