namespace MelodyBridge.Core;

public record MediaServerSyncReport
(
    int ResolvedCount,
    string[]? UnresolvedPaths,
    string? PlaylistId,
    string Message
);
