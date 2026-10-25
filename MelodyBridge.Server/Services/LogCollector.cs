using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Services;
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
    private long _version;

    /// <summary>
    /// Maximum number of entries retained in memory. Default 1000.
    /// </summary>
    public int MaxEntries { get; }

    public LogCollector(int maxEntries = 1000)
    {
        MaxEntries = maxEntries;
    }

    public void Log(CoreLogLevel level, string category, string message,
        string? detail = null, string? group = null, string? area = null)
    {
        // One stamp per entry: the Logs page keys its rows on it, so two
        // value-equal lines stay two rows. It also counts everything ever
        // collected, evicted entries included.
        var sequence = Interlocked.Increment(ref _nextId);
        var entry = new LogEntry(
            DateTime.UtcNow,
            level,
            category,
            message,
            detail,
            group,
            string.IsNullOrWhiteSpace(area) ? LogAreas.FromCategory(category) : area,
            sequence
        );

        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            Interlocked.Increment(ref _version);
        }
    }

    public IReadOnlyList<LogEntry> GetEntries()
    {
        // Insertion order is chronological; reverse for newest-first.
        // Ordering by Timestamp alone is not stable when entries share a
        // tick, which made the newest entry land mid-list.
        lock (_lock)
        {
            var list = _entries.ToList();
            list.Reverse();
            return list;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Interlocked.Increment(ref _version);
        }
    }

    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Total entries collected since startup (including evicted ones).
    /// </summary>
    public long TotalCollected => _nextId;
}
