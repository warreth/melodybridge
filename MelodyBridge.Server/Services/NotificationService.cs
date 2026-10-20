namespace MelodyBridge.Server.Services;

public sealed record AppNotification(
    string Message,
    string Level = "info",
    DateTime CreatedAtUtc = default,
    string? ActionLabel = null,
    string? ActionUrl = null);

/// <summary>
/// Tiny singleton notification bus: pages and services publish, MainLayout
/// renders the active toasts and the history panel. Errors and warnings
/// stay until the user closes them; success and info fade after a few
/// seconds. The history keeps the last entries so a dismissed toast can
/// still be found again.
/// </summary>
public sealed class NotificationService
{
    private static readonly TimeSpan DefaultAutoDismiss = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly List<AppNotification> _items = [];
    private readonly List<AppNotification> _history = [];
    private readonly int _capacity;
    private readonly int _historyCapacity;
    private readonly TimeSpan _autoDismissAfter;
    private readonly TimeSpan _problemDismissAfter;
    private int _unread;

    public event Action? Changed;

    /// <summary>Capacity caps the in-memory stack; autoDismissAfter is injectable so tests run fast.</summary>
    public NotificationService(
        int capacity = 20,
        TimeSpan? autoDismissAfter = null,
        int historyCapacity = 50,
        TimeSpan? problemDismissAfter = null)
    {
        _capacity = capacity;
        _historyCapacity = Math.Max(historyCapacity, capacity);
        _autoDismissAfter = autoDismissAfter ?? DefaultAutoDismiss;
        _problemDismissAfter = problemDismissAfter ?? TimeSpan.FromSeconds(12);
    }

    public void Info(string message, string? actionLabel = null, string? actionUrl = null)
        => Push(new AppNotification(message, "info", DateTime.UtcNow, actionLabel, actionUrl));

    public void Success(string message, string? actionLabel = null, string? actionUrl = null)
        => Push(new AppNotification(message, "success", DateTime.UtcNow, actionLabel, actionUrl));

    public void Warn(string message, string? actionLabel = null, string? actionUrl = null)
        => Push(new AppNotification(message, "warn", DateTime.UtcNow, actionLabel, actionUrl));

    public void Error(string message, string? actionLabel = null, string? actionUrl = null)
        => Push(new AppNotification(message, "error", DateTime.UtcNow, actionLabel, actionUrl));

    /// <summary>Silent variant for background listeners (download finished, job failed): only shows when the user enabled it in Settings, Notifications tab.</summary>
    public void Background(
        string message, string level = "info", string? actionLabel = null, string? actionUrl = null) =>
        Push(new AppNotification(message, level, DateTime.UtcNow, actionLabel, actionUrl));

    /// <summary>The toasts currently on screen, newest first.</summary>
    public IReadOnlyList<AppNotification> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    /// <summary>The last notifications, including ones already dismissed, newest first.</summary>
    public IReadOnlyList<AppNotification> History()
    {
        lock (_gate) return _history.ToList();
    }

    /// <summary>Entries added since the history panel was last opened.</summary>
    public int UnreadCount
    {
        get
        {
            lock (_gate) return _unread;
        }
    }

    public void MarkAllSeen()
    {
        lock (_gate) _unread = 0;
        Changed?.Invoke();
    }

    public void Dismiss(AppNotification item)
    {
        lock (_gate) _items.Remove(item);
        Changed?.Invoke();
    }

    public void ClearActive()
    {
        lock (_gate) _items.Clear();
        Changed?.Invoke();
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            _unread = 0;
        }
        Changed?.Invoke();
    }

    private void Push(AppNotification item)
    {
        lock (_gate)
        {
            _items.Insert(0, item);
            if (_items.Count > _capacity)
                _items.RemoveRange(_capacity, _items.Count - _capacity);

            _history.Insert(0, item);
            if (_history.Count > _historyCapacity)
                _history.RemoveRange(_historyCapacity, _history.Count - _historyCapacity);
            _unread++;
        }
        Changed?.Invoke();

        // Errors and warnings are the ones a reader must be able to act
        // on; they stay longer than a success but still fade so a stack of
        // sticky errors cannot cover the page forever. The history and the
        // bell keep them after the toast is gone.
        var delay = item.Level is "error" or "warn"
            ? _problemDismissAfter
            : _autoDismissAfter;

        // Auto-dismiss in the background: survives layout re-renders.
        // A manual Dismiss removes the item first, so this then no-ops.
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            lock (_gate) _items.Remove(item);
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }
}
