namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One slot pool shared by every track download in the process: playlist
/// runs, single-row clicks, manual picks and link downloads. The
/// download_max_concurrent setting used to be applied per run, so two
/// playlists starting together took that many tracks each. The pool makes
/// the number mean what the Advanced page says.
///
/// The limit is read from the settings on first use and re-applied at the
/// start of every playlist run, so a change is picked up by the next run
/// without a restart. Replacing the pool while old slots are still held is
/// fine: those holders release the pool they acquired from, and new
/// downloads count against the new one.
/// </summary>
public sealed class DownloadThrottle
{
    /// <summary>Used when the setting row is missing or unreadable.</summary>
    public const int DefaultLimit = 2;

    private readonly SettingsStore? _settings;
    private readonly SemaphoreSlim _configureLock = new(1, 1);
    private readonly object _lock = new();
    private SemaphoreSlim _gate;
    private int _limit;
    private bool _configured;

    public DownloadThrottle(SettingsStore settings)
        : this(settings, DefaultLimit, configured: false)
    {
    }

    /// <summary>Test seam: a fixed limit with no settings read.</summary>
    internal DownloadThrottle(int limit)
        : this(null, Math.Clamp(limit, 1, 8), configured: true)
    {
    }

    private DownloadThrottle(SettingsStore? settings, int limit, bool configured)
    {
        _settings = settings;
        _limit = limit;
        _gate = new SemaphoreSlim(limit, limit);
        _configured = configured;
    }

    /// <summary>The limit in force for new acquisitions.</summary>
    public int Limit
    {
        get { lock (_lock) return _limit; }
    }

    /// <summary>Applies a new limit to downloads that start from now on.</summary>
    public void Configure(int limit)
    {
        limit = Math.Clamp(limit, 1, 8);
        lock (_lock)
        {
            _configured = true;
            if (limit == _limit) return;
            _limit = limit;
            _gate = new SemaphoreSlim(limit, limit);
        }
    }

    /// <summary>Takes one slot; dispose the result to give it back.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await EnsureConfiguredAsync(ct);
        SemaphoreSlim gate;
        lock (_lock) gate = _gate;
        await gate.WaitAsync(ct);
        return new Slot(gate);
    }

    private async Task EnsureConfiguredAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_configured) return;
        }

        await _configureLock.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (_configured) return;
            }

            var limit = DefaultLimit;
            if (_settings is not null)
            {
                try
                {
                    var raw = await _settings.GetAsync(
                        "download_max_concurrent", DefaultLimit.ToString(), ct);
                    if (int.TryParse(raw, out var parsed)) limit = parsed;
                }
                catch
                {
                    // A settings read must never block a download; the
                    // default limit is a safe answer.
                }
            }
            Configure(limit);
        }
        finally
        {
            _configureLock.Release();
        }
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
            => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
