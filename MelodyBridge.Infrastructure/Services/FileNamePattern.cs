using System.Text.RegularExpressions;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The user's file name pattern for downloaded music files. One pattern
/// for the whole library, expanded per track and sanitized with the same
/// rules every file the app writes uses.
///
/// Tokens: {artist}, {title}, {album}, {track} (two digits, 1-based).
/// An empty or whitespace pattern means "keep the name the plugin chose".
/// </summary>
public static class FileNamePattern
{
    public const string SettingKey = "file_name_pattern";

    /// <summary>Artist first: the common library convention and what
    /// players sort on.</summary>
    public const string Default = "{artist} - {title}";

    /// <summary>The explicit "keep the plugin's name" choice. An empty
    /// settings value reads as missing, so this token is the way to say it.</summary>
    public const string SourceToken = "{source}";

    /// <summary>Separators left behind when a token is empty.</summary>
    private static readonly Regex LooseSeparator =
        new(@"(?:\s*[-_]\s*){2,}", RegexOptions.Compiled);

    /// <summary>
    /// Expands the pattern for one track. Returns null when the pattern is
    /// empty (keep the source name) or when nothing usable survives; the
    /// caller then keeps the current file name.
    /// </summary>
    public static string? Expand(
        string? pattern, string? artist, string? title, string? album = null, int? track = null)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        if (pattern.Trim().Equals(SourceToken, StringComparison.OrdinalIgnoreCase)) return null;

        var value = pattern
            .Replace("{artist}", artist?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", title?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{album}", album?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{track}", track is > 0 ? track.Value.ToString("00") : string.Empty,
                StringComparison.OrdinalIgnoreCase);

        // " - Title" and "Artist - " keep their meaning; "A -  - B" from an
        // empty album collapses to one separator.
        value = LooseSeparator.Replace(value, " - ");
        value = value.Trim().Trim('-', '_', '.', ' ');

        var safe = SafeFileNames.Sanitize(value, fallback: string.Empty);
        // A pattern that only held separators or dots is as unusable as an
        // empty one: fall back to the title instead of a file called "_".
        if (safe.Trim('_', '-', '.', ' ').Length == 0)
            safe = SafeFileNames.Sanitize(title, fallback: string.Empty);
        return safe.Length == 0 ? null : safe;
    }
}
