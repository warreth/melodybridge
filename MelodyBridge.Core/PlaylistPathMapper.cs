namespace MelodyBridge.Core;

/// <summary>
/// Applies a sync job's output path rules to one track path: the path
/// prefix remap first (a Docker mount, for example), then the extension
/// remap (a player that only accepts .mp3 while the library holds .flac).
/// Used by the M3U generator and every media server client so the four
/// outputs never drift apart.
/// </summary>
public static class PlaylistPathMapper
{
    /// <summary>
    /// Returns the path as the output consumer should see it. Null and
    /// empty paths come back unchanged; a rule that does not match is a
    /// no-op.
    /// </summary>
    public static string? Apply(string? path, PlaylistOutputOptions options)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var mapped = path;

        // Path remap: first matching prefix wins, case-insensitively.
        // The prefix ends at a directory boundary ("/music" claims
        // "/music/a" but never "/music2"), exactly what a mount path can
        // say about a sibling folder.
        if (options.PathRemap is { Count: > 0 })
        {
            foreach (var rule in options.PathRemap)
            {
                if (string.IsNullOrEmpty(rule.Key)) continue;
                if (!mapped.StartsWith(rule.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (mapped.Length > rule.Key.Length)
                {
                    var after = mapped[rule.Key.Length];
                    if (after is not '/' and not '\\') continue;
                }
                mapped = rule.Value + mapped[rule.Key.Length..];
                break;
            }
        }

        // Extension remap: the rule keys are extensions (".flac" or
        // "flac"); only the extension is replaced, never a path prefix.
        if (options.ExtensionRemap is { Count: > 0 })
        {
            var extension = Path.GetExtension(mapped);
            if (extension.Length > 0)
            {
                foreach (var rule in options.ExtensionRemap)
                {
                    if (string.IsNullOrEmpty(rule.Key)) continue;
                    if (!extension.TrimStart('.').Equals(
                            rule.Key.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                        continue;
                    var target = rule.Value.TrimStart('.');
                    if (target.Length > 0)
                        mapped = mapped[..^extension.Length] + "." + target;
                    break;
                }
            }
        }

        return mapped;
    }
}
