namespace MelodyBridge.Server.Services;

public sealed record AppNotification(
    string Message,
    string Level = "info",
    DateTime CreatedAtUtc = default);

/// <summary>
/// Tiny singleton toast bus: pages and services publish, MainLayout renders
/// and dismisses. No queueing on disk, no history - anything that must
/// survive a restart belongs in the log or the database.
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan DefaultAutoDismiss = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly List<AppNotification> _items = [];
    private readonly int _capacity;
    private readonly TimeSpan _autoDismissAfter;

    public event Action? Changed;

    /// <summary>Capacity caps the in-memory stack; autoDismissAfter is injectable so tests run fast.</summary>
    public NotificationService(int capacity = 20, TimeSpan? autoDismissAfter = null)
    {
        _capacity = capacity;
        _autoDismissAfter = autoDismissAfter ?? DefaultAutoDismiss;
    }

    public void Info(string message) => Push(new AppNotification(message, "info", DateTime.UtcNow));
    public void Success(string message) => Push(new AppNotification(message, "success", DateTime.UtcNow));
    public void Warn(string message) => Push(new AppNotification(message, "warn", DateTime.UtcNow));
    public void Error(string message) => Push(new AppNotification(message, "error", DateTime.UtcNow));

    /// <summary>Silent variant for background listeners (download finished, job failed): only shows when the user enabled it in Advanced.</summary>
    public void Background(string message, string level = "info") =>
        Push(new AppNotification(message, level, DateTime.UtcNow));

    public IReadOnlyList<AppNotification> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    public void Dismiss(AppNotification item)
    {
        lock (_gate) _items.Remove(item);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
        Changed?.Invoke();
    }

    private void Push(AppNotification item)
    {
        lock (_gate)
        {
            _items.Insert(0, item);
            if (_items.Count > _capacity)
                _items.RemoveRange(_capacity, _items.Count - _capacity);
        }
        Changed?.Invoke();

        // Auto-dismiss in the background: survives layout re-renders.
        // A manual Dismiss removes the item first, so this then no-ops.
        _ = Task.Delay(_autoDismissAfter).ContinueWith(_ =>
        {
            lock (_gate) _items.Remove(item);
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }
}
