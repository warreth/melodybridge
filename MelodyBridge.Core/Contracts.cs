namespace MelodyBridge.Core;

public interface ISourceProvider
{
    string Name { get; }
    Task<Playlist> GetPlaylistAsync(string sourceIdentifier);
}

public interface ILibraryScanner
{
    /// <summary>
    /// Scan the provided paths and update the database with found MELODY_IDs.
    /// </summary>
    Task ScanAsync(IEnumerable<ScanLocation> paths, CancellationToken cancellationToken = default);
}

public interface IPlaylistComposer
{
    /// <summary>
    /// Generate a playlist output based on the given playlist and search locations.
    /// </summary>
    Task ComposeAsync(Playlist playlist, IEnumerable<ScanLocation> searchLocations, PlaylistOutputOptions options, CancellationToken ct = default);
}

public interface IMediaServerSync
{
    string Name { get; }
    Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default);
}

public record PlaylistOutputOptions
(
    string OutputPath,
    bool UseRelativePaths,
    Dictionary<string, string>? PathRemap
);
