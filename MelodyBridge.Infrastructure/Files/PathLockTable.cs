namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// One lock per path. Two workers that write the same file name take
/// the same lock, so the second one waits and sees what the first one
/// left there instead of writing over it.
/// </summary>
public sealed class PathLockTable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The table the app's workers share.</summary>
    public static PathLockTable Shared { get; } = new();

    /// <summary>
    /// Takes the lock for the path. The lock is released when the scope
    /// is disposed.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string path, CancellationToken ct = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out entry!))
            {
                entry = new Entry();
                _entries[path] = entry;
            }
            // Counted before the wait, so the entry stays alive for every
            // worker that is in the queue.
            entry.Holders++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Forget(path, entry);
            throw;
        }
        return new Scope(this, path, entry);
    }

    private void Release(string path, Entry entry)
    {
        entry.Semaphore.Release();
        Forget(path, entry);
    }

    private void Forget(string path, Entry entry)
    {
        lock (_gate)
        {
            entry.Holders--;
            if (entry.Holders > 0) return;
            if (_entries.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(path);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Holders { get; set; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly PathLockTable _table;
        private readonly string _path;
        private readonly Entry _entry;
        private bool _released;

        public Scope(PathLockTable table, string path, Entry entry)
        {
            _table = table;
            _path = path;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _table.Release(_path, _entry);
        }
    }
}
