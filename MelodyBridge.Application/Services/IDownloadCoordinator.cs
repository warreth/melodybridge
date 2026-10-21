using MelodyBridge.Core;

namespace MelodyBridge.Application.Services;

/// <summary>
/// The UI-facing view of the download coordinator: current run state and
/// the two actions a page takes on a playlist. Kept separate so pages can
/// be exercised with a coordinator that yields at a chosen point.
/// </summary>
public interface IDownloadCoordinator : IDownloadTrigger
{
    /// <summary>The live or last run for a playlist, or null when it never ran.</summary>
    DownloadRun? RunFor(string playlistId);

    /// <summary>All live and last runs, newest first, for badge refreshes.</summary>
    IReadOnlyList<DownloadRun> Snapshot();

    /// <summary>True while a run for the playlist is running, paused or cancelling.</summary>
    bool IsActive(string playlistId);
}
