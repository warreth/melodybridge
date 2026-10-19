namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Tiny TTL cache for a plugin's availability verdict. The waterfall
/// probes every enabled plugin per track, and the Plugins page re-checks
/// on a timer; without a cache the HTTP plugins would hammer their
/// mirrors with availability sweeps. A positive verdict is trusted for
/// longer than a negative one, so a fixed outage clears quickly.
/// </summary>
internal sealed class AvailabilityCache
{
    private static readonly TimeSpan AvailableTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnavailableTtl = TimeSpan.FromSeconds(30);

    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private bool _result;

    /// <summary>True when a still-valid verdict exists; returns it in <paramref name="available"/>.</summary>
    public bool TryGet(out bool available)
    {
        available = _result;
        var ttl = _result ? AvailableTtl : UnavailableTtl;
        return DateTimeOffset.UtcNow - _checkedAt < ttl;
    }

    /// <summary>Stores a fresh verdict.</summary>
    public void Store(bool available)
    {
        _result = available;
        _checkedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Drops the stored verdict. Settings that change a login or a host
    /// must be checked for real on the next probe, not answered from the
    /// old cache.
    /// </summary>
    public void Invalidate() => _checkedAt = DateTimeOffset.MinValue;
}
