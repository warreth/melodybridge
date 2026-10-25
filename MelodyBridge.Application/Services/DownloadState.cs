using System.Collections.Concurrent;
using MelodyBridge.Core;

namespace MelodyBridge.Application.Services;

/// <summary>
/// Live download state for the UI: the last progress per melody id plus why
/// the last attempt produced nothing. The manager is a singleton that lives
/// for the whole process, so both maps are bounded: finished entries age out,
/// and a count cap keeps a long-running install from growing forever. A
/// failure stays readable (callers may look it up more than once) but not
/// forever.
/// </summary>
internal sealed class DownloadState
{
    /// <summary>Most progress entries kept; terminal ones are dropped oldest-first.</summary>
    internal const int MaxEntries = 512;

    /// <summary>How long a finished entry or a failure stays readable.</summary>
    internal static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(30);

    private readonly record struct Timed<T>(T Value, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Timed<DownloadProgress>> _progress = new();
    private readonly ConcurrentDictionary<string, Timed<string>> _failures = new();

    public void SetProgress(DownloadProgress progress) => SetProgress(progress, DateTimeOffset.UtcNow);

    internal void SetProgress(DownloadProgress progress, DateTimeOffset now)
    {
        _progress[progress.MelodyId] = new Timed<DownloadProgress>(progress, now);
        Prune(now);
    }

    public void SetFailure(string melodyId, string reason) => SetFailure(melodyId, reason, DateTimeOffset.UtcNow);

    internal void SetFailure(string melodyId, string reason, DateTimeOffset now)
    {
        _failures[melodyId] = new Timed<string>(reason, now);
        Prune(now);
    }

    public void ClearFailure(string melodyId) => _failures.TryRemove(melodyId, out _);

    /// <summary>The recorded failure, or null; safe to call more than once.</summary>
    public string? LastFailure(string melodyId)
    {
        Prune(DateTimeOffset.UtcNow);
        return _failures.TryGetValue(melodyId, out var entry) ? entry.Value : null;
    }

    /// <summary>Every live progress entry, newest writes included. The
    /// overview sorts on the write time, so each entry carries its own.</summary>
    public IReadOnlyList<DownloadProgress> Snapshot()
    {
        Prune(DateTimeOffset.UtcNow);
        return _progress.Values
            .Select(entry => entry.Value with { At = entry.At })
            .ToList();
    }

    /// <summary>
    /// Drops finished progress entries and failures past the TTL, then trims
    /// terminal progress entries oldest-first while over the cap. In-flight
    /// entries (searching, downloading) are never dropped: they are the ones
    /// the UI is actually showing.
    /// </summary>
    internal void Prune(DateTimeOffset now)
    {
        var cutoff = now - EntryTtl;

        foreach (var (id, entry) in _progress)
        {
            if (entry.At < cutoff && IsTerminal(entry.Value.Status))
                _progress.TryRemove(id, out _);
        }

        foreach (var (id, entry) in _failures)
        {
            if (entry.At < cutoff)
                _failures.TryRemove(id, out _);
        }

        if (_progress.Count <= MaxEntries) return;

        foreach (var (id, entry) in _progress
            .Where(p => IsTerminal(p.Value.Value.Status))
            .OrderBy(p => p.Value.At))
        {
            if (_progress.Count <= MaxEntries) break;
            _progress.TryRemove(id, out _);
        }
    }

    private static bool IsTerminal(string status)
        => status is "done" or "failed";
}
