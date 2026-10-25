namespace MelodyBridge.Core;

/// <summary>
/// The track claims that are live right now in this process. A track row
/// in "in_progress" is held by a download that is running: a single-track
/// click or a batch worker. The row survives a crash, so the row alone
/// cannot tell a live download from a dead one. This registry holds the
/// in-process truth, so a batch that starts can tell the two apart before
/// it recovers stale claims.
/// </summary>
public interface ITrackClaimRegistry
{
    /// <summary>True while a download in this process holds the track.</summary>
    bool IsLive(int trackId);

    /// <summary>How many tracks have a live claim right now.</summary>
    int ActiveCount { get; }

    /// <summary>
    /// Marks the track as claimed for the lifetime of the returned scope.
    /// The scope is disposed when the download ends, however it ends.
    /// Register before the row is claimed, so a reader can never see an
    /// in_progress row that the registry does not know about yet.
    /// </summary>
    IDisposable Claim(int trackId);
}
