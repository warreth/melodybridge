namespace MelodyBridge.Core;

/// <summary>
/// The outcome of one media-server sync. <see cref="Succeeded"/> is false
/// when the sync itself failed (unreachable server, rejected create or
/// update, an exception); the job runner turns that into a failed run
/// instead of a silent "completed".
/// </summary>
public record MediaServerSyncReport
(
    int ResolvedCount,
    string[]? UnresolvedPaths,
    string? PlaylistId,
    string Message,
    bool Succeeded = true
);
