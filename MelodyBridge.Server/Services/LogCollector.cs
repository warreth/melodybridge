using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Services;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Singleton in-memory log collector. Thread-safe. The live view is bounded
/// at <see cref="MaxEntries"/>; warnings and errors are also kept in a
/// separate archive so an export still carries the full problem history
/// after the live buffer rolled over.
/// </summary>
public sealed class LogCollector : ILogCollector
{
    private readonly List<LogEntry> _entries = new();
    private readonly List<LogEntry> _problemEntries = new();
    private readonly object _lock = new();
    private int _nextId;
    private long _version;

    /// <summary>
    /// Maximum number of entries retained in the live buffer. Default 1000.
    /// </summary>
    public int MaxEntries { get; }

    /// <summary>
    /// Maximum number of warning/error entries kept outside the live buffer.
    /// Default 5000; a runaway warning loop must not grow without bound.
    /// </summary>
    public int MaxProblemEntries { get; }

    public LogCollector(int maxEntries = 1000, int maxProblemEntries = 5000)
    {
        MaxEntries = maxEntries;
        MaxProblemEntries = maxProblemEntries;
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

            if (level >= CoreLogLevel.Warn)
            {
                _problemEntries.Add(entry);
                if (_problemEntries.Count > MaxProblemEntries)
                    _problemEntries.RemoveRange(0, _problemEntries.Count - MaxProblemEntries);
            }
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

    /// <summary>
    /// The full export view: live entries plus archived warnings/errors,
    /// newest first. Entries still in both buffers appear once.
    /// </summary>
    public IReadOnlyList<LogEntry> GetExportEntries()
    {
        lock (_lock)
        {
            var bySequence = new Dictionary<long, LogEntry>(_entries.Count + _problemEntries.Count);
            foreach (var entry in _entries) bySequence[entry.Sequence] = entry;
            foreach (var entry in _problemEntries) bySequence[entry.Sequence] = entry;
            return bySequence.Values
                .OrderByDescending(entry => entry.Sequence)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _problemEntries.Clear();
            Interlocked.Increment(ref _version);
        }
    }

    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Total entries collected since startup (including evicted ones).
    /// </summary>
    public long TotalCollected => _nextId;
}
