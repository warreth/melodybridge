namespace MelodyBridge.Core;

/// <summary>
/// Display metadata read from a song link before it is downloaded. The
/// resolver is best effort: a link it cannot inspect leaves the caller
/// with whatever the user typed.
/// </summary>
public record UrlTrackMetadata(string? Artist, string? Title, TimeSpan? Duration);

/// <summary>
/// Reads artist, title and length from a song link. Implemented with
/// yt-dlp in Infrastructure; a fake keeps the manual download tests
/// independent of the network and the binary.
/// </summary>
public interface IUrlMetadataResolver
{
    Task<UrlTrackMetadata?> ResolveAsync(string url, CancellationToken ct = default);
}
