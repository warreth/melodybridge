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
    /// The key a path is locked under. On a volume that folds case, two
    /// spellings of one name are one file, so they share one lock. The
    /// rest of the path is spelled as it is: two different files are two
    /// different locks.
    /// </summary>
    public static string KeyFor(string path, bool foldCase)
        => foldCase ? path.ToLowerInvariant() : path;

    /// <summary>
    /// The key a path is locked under, with the rule of the volume that
    /// holds it.
    /// </summary>
    public static string KeyFor(string path)
        => KeyFor(path, FileSystemCase.FoldsCase(
            Path.GetDirectoryName(path) ?? path));

    /// <summary>
    /// Takes the lock for the path. The lock is released when the scope
    /// is disposed.
    /// </summary>
    public Task<IDisposable> AcquireAsync(string path, CancellationToken ct = default)
        => AcquireAsync(path, KeyFor(path), ct);

    /// <summary>
    /// Takes the lock for the path, with the case rule of the volume
    /// given by the caller.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(
        string path, bool foldCase, CancellationToken ct = default)
        => await AcquireAsync(path, KeyFor(path, foldCase), ct);

    private async Task<IDisposable> AcquireAsync(string path, string key, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
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
            Forget(key, entry);
            throw;
        }
        return new Scope(this, key, entry);
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        Forget(key, entry);
    }

    private void Forget(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.Holders--;
            if (entry.Holders > 0) return;
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
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
        private readonly string _key;
        private readonly Entry _entry;
        private bool _released;

        public Scope(PathLockTable table, string key, Entry entry)
        {
            _table = table;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            _table.Release(_key, _entry);
        }
    }
}
