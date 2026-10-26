using System.Collections.Generic;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One filename sanitizer for files MelodyBridge writes itself: M3U
/// output, CSV downloads and zip entries. It is deliberately stricter
/// than the host OS so a name that came from another platform stays one
/// safe file name everywhere: separators, control characters, Windows
/// reserved names and trailing dots or spaces are handled here.
/// </summary>
public static class SafeFileNames
{
    private static readonly char[] Forbidden = ['"', '<', '>', '|', ':', '*', '?', '\\', '/', '\0'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A single safe file name, not a path. A name that sanitizes to
    /// nothing, or to a dot segment such as "..", falls back instead of
    /// creating a traversal-shaped entry.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "playlist")
    {
        var source = (name ?? string.Empty).Trim();
        if (source.Length == 0) return fallback;

        var cleaned = new string(source.Select(c =>
            char.IsControl(c) || Forbidden.Contains(c) ? '_' : c).ToArray()).Trim();
        cleaned = cleaned.TrimEnd('.', ' ').Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..") return fallback;

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (Reserved.Contains(stem)) cleaned = "_" + cleaned;

        if (cleaned.Length > 120)
            cleaned = cleaned[..120].TrimEnd('.', ' ');

        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>
    /// A unique file name inside one folder for a list of paths already in
    /// use. The same numbered suffix keeps quick publish and the manual
    /// wizard from choosing the same M3U file.
    /// </summary>
    public static string UniqueFile(
        string directory, string name, string extension, IEnumerable<string> usedPaths)
    {
        var baseName = Sanitize(name);
        var folder = Path.TrimEndingDirectorySeparator(directory);
        var suffixExtension = extension.StartsWith('.') ? extension : "." + extension;
        var used = usedPaths.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = Path.Combine(folder, baseName + suffixExtension);
        for (var suffix = 2; used.Contains(PathKey(candidate)); suffix++)
            candidate = Path.Combine(folder, $"{baseName} ({suffix}){suffixExtension}");
        return candidate;
    }

    /// <summary>
    /// A unique variant of an already built path (for example one that came
    /// from a rule profile's file name pattern). The suffix goes before the
    /// extension, so a custom pattern keeps its shape.
    /// </summary>
    public static string UniquePath(string path, IEnumerable<string> usedPaths)
    {
        var used = usedPaths.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(PathKey(path))) return path;

        var folder = Path.GetDirectoryName(path) ?? "";
        var extension = Path.GetExtension(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        var candidate = path;
        for (var suffix = 2; used.Contains(PathKey(candidate)); suffix++)
            candidate = Path.Combine(folder, $"{baseName} ({suffix}){extension}");
        return candidate;
    }

    /// <summary>One comparison form for paths that may use either separator.</summary>
    public static string PathKey(string path)
        => path.Replace('\\', '/').TrimEnd('/');
}
