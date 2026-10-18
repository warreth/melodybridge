using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// Moves tracked library files between folders without ever overwriting:
/// the destination folder is created, an occupied destination is skipped,
/// and one failure is logged and left for the caller to count. Shared by
/// the startup reconciler (loose files into the playlist folder) and the
/// settings path mover (the whole library to a new root).
/// </summary>
internal static class FileRelocator
{
    /// <summary>One move, best effort: false when anything stood in the way.</summary>
    public static bool TryMove(string source, string destination, ILogger logger)
    {
        if (PathsEqual(source, destination)) return false;
        try
        {
            if (File.Exists(destination))
            {
                logger.LogInformation(
                    "Library move: {Source} not moved, {Destination} already exists", source, destination);
                return false;
            }
            var folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.Move(source, destination);
            logger.LogInformation("Library move: moved {Source} to {Destination}", source, destination);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Library move: could not move {Source}", source);
            return false;
        }
    }

    /// <summary>True when the path sits inside the folder (not the folder itself).</summary>
    public static bool IsUnder(string path, string folder)
    {
        try
        {
            var root = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, PathComparison);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Path equality ignoring a trailing separator; case-insensitive on Windows/macOS.</summary>
    public static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        return string.Equals(
            first.Trim().TrimEnd('/', '\\'),
            second.Trim().TrimEnd('/', '\\'),
            PathComparison);
    }

    /// <summary>True when a library can move from one root to another:
    /// both given, different, and not nested inside each other.</summary>
    public static bool CanMove(string? oldRoot, string? newRoot)
        => !string.IsNullOrWhiteSpace(oldRoot)
           && !string.IsNullOrWhiteSpace(newRoot)
           && !PathsEqual(oldRoot, newRoot)
           && !IsUnder(newRoot, oldRoot)
           && !IsUnder(oldRoot, newRoot);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
