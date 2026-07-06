namespace MelodyBridge.Core;

/// <summary>
/// Represents a single search result from a music provider.
/// </summary>
public record SearchResult(
    string Title,
    string Artist,
    string? Album,
    string Url,
    Platform SourcePlatform,
    IReadOnlyList<TrackQuality> AvailableQualities
);

/// <summary>
/// Detailed track information from a provider.
/// </summary>
public record TrackInfo(
    string Title,
    string Artist,
    string? Album,
    string? CoverUrl,
    string Url,
    Platform SourcePlatform,
    IReadOnlyList<TrackQuality> AvailableQualities
);

/// <summary>
/// Result of a download operation.
/// </summary>
public record DownloadResult(
    bool Success,
    string? FilePath,
    string? ErrorMessage,
    TrackQuality? ActualQuality
);

/// <summary>
/// Metadata describing a music provider plugin.
/// </summary>
public record ProviderMetadata(
    string Id,
    string Name,
    string Description,
    string Icon,
    IReadOnlyList<Platform> SupportedPlatforms,
    IReadOnlyList<TrackQuality> SupportedQualities
);

/// <summary>
/// Modular provider interface for music downloading services.
/// Each provider handles search, track resolution, and downloading
/// for one or more streaming platforms.
/// </summary>
public interface IMusicProvider
{
    /// <summary>Unique identifier for this provider.</summary>
    string Id { get; }

    /// <summary>Human-readable name.</summary>
    string Name { get; }

    /// <summary>Short description of what this provider does.</summary>
    string Description { get; }

    /// <summary>Emoji or icon string for UI display.</summary>
    string Icon { get; }

    /// <summary>Supported streaming platforms.</summary>
    IReadOnlyList<Platform> SupportedPlatforms { get; }

    /// <summary>Supported audio qualities and formats.</summary>
    IReadOnlyList<TrackQuality> SupportedQualities { get; }

    /// <summary>Search for tracks on the supported platform(s).</summary>
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default);

    /// <summary>Get detailed information about a track from its URL.</summary>
    Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Download a track to the specified output directory.
    /// Returns the local file path on success.
    /// </summary>
    Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default);
}
