namespace MelodyBridge.Core;

/// <summary>
/// Starts a download run for a playlist from the outside, used by the
/// auto-sync scheduler when a playlist opted in. Implemented by the
/// download coordinator; the interface lives in Core so Infrastructure
/// services never have to reference Application.
/// </summary>
public interface IDownloadTrigger
{
    /// <summary>
    /// True when the playlist still has tracks to download: never
    /// downloaded, pending or failed.
    /// </summary>
    Task<bool> HasPendingWorkAsync(string playlistId, CancellationToken ct = default);

    /// <summary>Starts or resumes a run; a no-op while one is already active.</summary>
    void Start(string playlistId);
}
