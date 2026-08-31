namespace MelodyBridge.Infrastructure.Services;

using MatchConfidence = MelodyBridge.Core.MatchConfidence;

/// <summary>
/// Fuzzy comparison between requested metadata and a search hit.
/// Normalizes casing, punctuation, featuring-credits, live/remix markers and
/// unicode before scoring; the token-based ratio is deliberately simple and
/// has no external dependencies.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Scores a hit: High when title and artist both match well, otherwise
    /// Low. Every hit below the thresholds is returned as Low, never thrown
    /// away: the waterfall decides, the user gets to see the doubt.
    /// </summary>
    public static Core.MatchConfidence Confidence(
        string requestedArtist, string requestedTitle,
        string? hitArtist, string? hitTitle)
    {
        var titleScore = Similarity(requestedTitle, hitTitle);
        var artistScore = Similarity(requestedArtist, hitArtist);

        // A clean title match with a plausible artist is enough; artists
        // appear in many spellings and featuring credits on ripper sites.
        if (titleScore >= 0.80 && artistScore >= 0.55) return MatchConfidence.High;
        if (titleScore >= 0.70 && artistScore >= 0.75) return MatchConfidence.High;
        return MatchConfidence.Low;
    }

    /// <summary>
    /// Token-set similarity in [0,1]: both strings are split into words, the
    /// overlap is measured against the shorter side. Order, casing and
    /// punctuation do not matter.
    /// </summary>
    public static double Similarity(string a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Count == 0 || right.Count == 0)
            return 0;

        var matches = 0;
        var remaining = new HashSet<string>(right, StringComparer.Ordinal);
        foreach (var token in left)
        {
            if (!remaining.Remove(token)) continue;
            matches++;
        }

        return (double)2 * matches / (left.Count + right.Count);
    }

    /// <summary>Lowercase, de-punctuated, de-accented, stop-word-cleaned tokens.</summary>
    private static List<string> Tokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();

        var normalized = s.ToLowerInvariant()
            .Replace("feat.", " ", StringComparison.Ordinal)
            .Replace("ft.", " ", StringComparison.Ordinal)
            .Replace("feat", " ", StringComparison.Ordinal)
            .Normalize(System.Text.NormalizationForm.FormD);

        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var ch in normalized)
        {
            // FormD decomposes accents into base letters + combining marks;
            // combining marks are dropped, letters and digits kept as-is.
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            else if (char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                Flush(builder, tokens);
            }
        }
        Flush(builder, tokens);

        return tokens
            .Where(t => !StopWords.Contains(t))
            .ToList();
    }

    private static void Flush(System.Text.StringBuilder builder, List<string> into)
    {
        if (builder.Length > 0)
        {
            into.Add(builder.ToString());
            builder.Clear();
        }
    }

    /// <summary>Words that carry no meaning for track identity.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "feat", "featuring", "official",
        "video", "audio", "lyrics", "hq", "hd", "remastered", "version",
        "single", "edit",
    };
}
