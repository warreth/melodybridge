namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>Jellyfin settings the sync needs beyond URL and API key.</summary>
public record JellyfinSyncOptions
{
    /// <summary>User whose favorites receive the liked songs.</summary>
    public string? UserId { get; init; }
}
