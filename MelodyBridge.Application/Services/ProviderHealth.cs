using System.Collections.Concurrent;

namespace MelodyBridge.Application.Services;

/// <summary>
/// Rolling health per downloader plugin: the waterfall asks before it
/// spends time on a provider. Three consecutive failed attempts open the
/// circuit (the provider is skipped without a call) for a cooldown, after
/// which a single probe per interval decides whether it closes again. A
/// degraded provider (mostly failures over its recent window) is demoted
/// behind healthy ones but still tried.
///
/// State is in memory only: a restart starts every provider neutral. That
/// is deliberate, a provider that was down yesterday deserves a fresh
/// chance, and the per-plugin availability caches already remember the
/// very short term.
/// </summary>
public sealed class ProviderHealth
{
    private const int WindowSize = 20;
    private const int FailuresToOpen = 3;
    private static readonly TimeSpan OpenCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public readonly Queue<bool> Window = new();
        public int ConsecutiveFailures;
        public DateTimeOffset? OpenUntil;
        public DateTimeOffset LastProbeAt = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<DateTimeOffset> _now;

    /// <summary>A clock override keeps the cooldown tests deterministic.</summary>
    public ProviderHealth(Func<DateTimeOffset>? clock = null)
        => _now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// True when the provider should be skipped without a call: the
    /// circuit is open, or the cooldown passed but another probe already
    /// started within the probe interval. A caller that got false here is
    /// expected to report an outcome (success or failure) afterwards.
    /// </summary>
    public bool ShouldSkip(string pluginId)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new Entry());
        lock (entry)
        {
            if (entry.OpenUntil is not { } until) return false;

            var now = _now();
            if (now < until) return true;

            // Cooldown passed: half-open. One probe per interval, so a
            // caller that never reported back cannot wedge the circuit.
            if (now - entry.LastProbeAt < ProbeInterval) return true;
            entry.LastProbeAt = now;
            return false;
        }
    }

    /// <summary>
    /// True when recent attempts mostly failed (more than half of at
    /// least four): the provider still runs, but after healthy ones.
    /// </summary>
    public bool IsDegraded(string pluginId)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new Entry());
        lock (entry)
            return entry.Window.Count >= 4
                && entry.Window.Count(success => !success) * 2 > entry.Window.Count;
    }

    /// <summary>Recent success ratio in [0,1]; 1.0 while unknown.</summary>
    public double Score(string pluginId)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new Entry());
        lock (entry)
            return entry.Window.Count == 0
                ? 1.0
                : entry.Window.Count(success => success) / (double)entry.Window.Count;
    }

    /// <summary>A download that passed every gate: the provider works.</summary>
    public void RecordSuccess(string pluginId)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new Entry());
        lock (entry)
        {
            Push(entry, success: true);
            entry.ConsecutiveFailures = 0;
            entry.OpenUntil = null;
        }
    }

    /// <summary>A failed attempt (error, timeout, junk file, budget): one strike.</summary>
    public void RecordFailure(string pluginId)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new Entry());
        lock (entry)
        {
            Push(entry, success: false);
            entry.ConsecutiveFailures++;
            if (entry.ConsecutiveFailures >= FailuresToOpen)
                entry.OpenUntil = _now() + OpenCooldown;
        }
    }

    private static void Push(Entry entry, bool success)
    {
        entry.Window.Enqueue(success);
        while (entry.Window.Count > WindowSize)
            entry.Window.Dequeue();
    }
}
