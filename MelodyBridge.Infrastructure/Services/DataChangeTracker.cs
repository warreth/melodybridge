namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// A monotonic counter over the stored library state, bumped by every
/// write that changes what an open page shows (downloads, quality and
/// dedup decisions, removals, snapshot saves, scans and reconciliation).
/// Pages poll <see cref="Version"/> on a timer and reload only when it
/// moved, so a change made by another circuit or a background service
/// still reaches them without re-querying the database every second.
/// </summary>
public sealed class DataChangeTracker
{
    private long _version;

    /// <summary>The current stamp; unchanged while nothing wrote.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Marks one committed change.</summary>
    public void Bump() => Interlocked.Increment(ref _version);
}
