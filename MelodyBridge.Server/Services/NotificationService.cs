namespace MelodyBridge.Server.Services;

/// <summary>
/// One notification. Normal entries only appear as toasts. Important
/// entries also stay in the bell until the user dismisses them or the
/// condition resolves. Key groups replacements of the same condition.
/// </summary>
public sealed record AppNotification(
    string Message,
    string Level = "info",
    DateTime CreatedAtUtc = default,
    string? ActionLabel = null,
    string? ActionUrl = null,
    bool Important = false,
    string? Key = null);

/// <summary>
/// Tiny singleton notification bus: pages and services publish, MainLayout
/// renders the active toasts and the important-notification panel.
///
/// Normal success/info/warn/error toasts fade after a few seconds and are
/// not kept: the bell is for things that still need a decision (plugin
/// logins, failed sync jobs, background failures and updates). Important
/// entries survive until dismissed or resolved by key, so a noisy log does
/// not bury them and a normal "saved" message does not pretend to be news.
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

    /// <summary>
    /// A notification that belongs in the bell: plugin login problems,
    /// failed jobs, background failures and updates. The toast still fades
    /// after its normal delay; the entry stays until dismissed or resolved.
    /// A repeated key replaces the previous entry instead of stacking.
    /// </summary>
    public void Important(
        string message, string level = "warn", string? actionLabel = null,
        string? actionUrl = null, string? key = null)
        => Push(new AppNotification(
            message, level, DateTime.UtcNow, actionLabel, actionUrl, Important: true, Key: key));

    /// <summary>Silent variant for background listeners (download finished, job failed): only shows when the user enabled it in Settings, Notifications tab.</summary>
    public void Background(
        string message, string level = "info", string? actionLabel = null, string? actionUrl = null,
        bool important = false, string? key = null) =>
        Push(new AppNotification(
            message, level, DateTime.UtcNow, actionLabel, actionUrl, important, key));

    /// <summary>The toasts currently on screen, newest first.</summary>
    public IReadOnlyList<AppNotification> Snapshot()
    {
        lock (_gate) return _items.ToList();
    }

    /// <summary>The important entries in the bell, newest first. Normal toasts are not kept here.</summary>
    public IReadOnlyList<AppNotification> History()
    {
        lock (_gate) return _history.ToList();
    }

    /// <summary>Important entries added since the bell was last opened.</summary>
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

    /// <summary>Dismisses one toast. The bell entry, if any, stays.</summary>
    public void Dismiss(AppNotification item)
    {
        lock (_gate) _items.Remove(item);
        Changed?.Invoke();
    }

    /// <summary>Dismisses one bell entry and its toast, if it is still visible.</summary>
    public void DismissImportant(AppNotification item)
    {
        lock (_gate)
        {
            _items.Remove(item);
            if (_history.Remove(item)) _unread = Math.Max(0, _unread - 1);
        }
        Changed?.Invoke();
    }

    /// <summary>Removes the entry for a resolved condition, for example a fixed plugin login.</summary>
    public void Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var removedHistory = 0;
        lock (_gate)
        {
            removedHistory = _history.RemoveAll(item =>
                string.Equals(item.Key, key, StringComparison.Ordinal));
            _items.RemoveAll(item =>
                string.Equals(item.Key, key, StringComparison.Ordinal));
            if (removedHistory > 0) _unread = Math.Max(0, _unread - removedHistory);
        }
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

            if (item.Important)
            {
                var replaced = false;
                if (!string.IsNullOrWhiteSpace(item.Key))
                {
                    replaced = _history.RemoveAll(existing =>
                        string.Equals(existing.Key, item.Key, StringComparison.Ordinal)) > 0;
                    _items.RemoveAll(existing => !ReferenceEquals(existing, item)
                        && string.Equals(existing.Key, item.Key, StringComparison.Ordinal));
                }

                _history.Insert(0, item);
                if (_history.Count > _historyCapacity)
                    _history.RemoveRange(_historyCapacity, _history.Count - _historyCapacity);
                if (!replaced) _unread++;
            }
        }
        Changed?.Invoke();

        // Errors and warnings are the ones a reader must be able to act
        // on; they stay longer than a success but still fade so a stack of
        // sticky errors cannot cover the page forever. The bell keeps the
        // important ones after the toast is gone.
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
