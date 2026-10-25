using System.Collections.Concurrent;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The claims held by downloads that are running in this process. One
/// instance for the whole app: the download pipeline is scoped, but a
/// claim belongs to the process, not to a request. A track can be claimed
/// twice (a batch worker and a single-track click race for it), so every
/// claim counts and the track is only free when the last one is released.
/// </summary>
public sealed class LiveTrackClaims : ITrackClaimRegistry
{
    private readonly ConcurrentDictionary<int, int> _claims = new();

    /// <summary>The number of live claims on the track.</summary>
    public int Count(int trackId) => _claims.TryGetValue(trackId, out var count) ? count : 0;

    public bool IsLive(int trackId) => _claims.ContainsKey(trackId);

    public int ActiveCount => _claims.Count;

    public IDisposable Claim(int trackId)
    {
        _claims.AddOrUpdate(trackId, 1, (_, count) => count + 1);
        return new Scope(this, trackId);
    }

    private void Release(int trackId)
    {
        // The count only goes down when the last claim ends. The compare
        // and swap keeps a claim that arrives at the same time from being
        // counted away.
        while (_claims.TryGetValue(trackId, out var count))
        {
            if (count <= 1)
            {
                if (_claims.TryRemove(new KeyValuePair<int, int>(trackId, count)))
                    return;
                continue; // the count changed, look again
            }
            if (_claims.TryUpdate(trackId, count - 1, count))
                return;
        }
    }

    private sealed class Scope(LiveTrackClaims owner, int trackId) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            owner.Release(trackId);
        }
    }
}
