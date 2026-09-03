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
        var artistScore = ArtistScore(requestedArtist, hitArtist);

        // A clean title match with a plausible artist is enough; artists
        // appear in many spellings and featuring credits on ripper sites.
        if (titleScore >= 0.80 && artistScore >= 0.55) return MatchConfidence.High;
        if (titleScore >= 0.70 && artistScore >= 0.75) return MatchConfidence.High;
        return MatchConfidence.Low;
    }

    /// <summary>
    /// Continuous score in [0,1] for ranking several candidate hits for
    /// the same request: title similarity weighted over artist
    /// similarity. Confidence bands are too coarse to pick a winner
    /// between an exact match and a remix of it.
    /// </summary>
    public static double Score(
        string requestedArtist, string requestedTitle,
        string? hitArtist, string? hitTitle)
        => 0.7 * Similarity(requestedTitle, hitTitle)
         + 0.3 * ArtistScore(requestedArtist, hitArtist);

    /// <summary>
    /// Artist similarity with channel-name tolerance: a hit artist that
    /// contains the requested artist as a whole word scores full marks,
    /// because uploaders decorate names ("RegardVEVO", "Regard - Topic").
    /// </summary>
    private static double ArtistScore(string requested, string? hit)
    {
        var baseScore = Similarity(requested, hit);
        if (baseScore >= 0.55) return baseScore;
        return ContainsWholeWord(hit, requested) ? 1.0 : baseScore;
    }

    /// <summary>True when <paramref name="haystack"/> contains the needle
    /// surrounded by non-letter characters ("RegardVEVO" does not contain
    /// the whole word "Regard", "Regard VEVO" does).</summary>
    private static bool ContainsWholeWord(string? haystack, string? needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            return false;
        var span = haystack.AsSpan();
        while (!span.IsEmpty)
        {
            var i = span.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            var after = i + needle.Length;
            var beforeOk = i == 0 || !char.IsLetter(span[i - 1]);
            var afterOk = after == span.Length || !char.IsLetter(span[after]);
            if (beforeOk && afterOk) return true;
            span = span[Math.Min(after, span.Length)..];
        }
        return false;
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
