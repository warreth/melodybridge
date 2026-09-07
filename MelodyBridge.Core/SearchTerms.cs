namespace MelodyBridge.Core;

/// <summary>
/// Shared search-term cleanup for provider queries. Every provider gets
/// the same artist/title text from a playlist, but the platforms index
/// clean titles: parenthetical suffixes like "(Original Mix)", featuring
/// tags and diacritics all reduce the hit rate when passed verbatim.
/// Normalizing once, in the core, keeps every plugin honest without each
/// one growing its own regex museum.
/// </summary>
public static partial class SearchTerms
{
    /// <summary>
    /// Clean title text for provider search: drops parenthetical/bracket
    /// clutter and featuring tags, folds diacritics to their base
    /// letters and collapses whitespace. Designed to never empty the
    /// term: when everything would be stripped, the original title
    /// (folded, whitespace-collapsed) is kept so the query stays
    /// meaningful.
    /// </summary>
    public static string CleanTitle(string title)
    {
        var cleaned = DropFeaturing(DropParentheticals(title));
        cleaned = FoldDiacritics(cleaned);
        cleaned = CollapseWhitespace(cleaned).Trim();
        return cleaned.Length == 0 ? CollapseWhitespace(FoldDiacritics(title)).Trim() : cleaned;
    }

    /// <summary>
    /// Clean artist text: featuring tags ("A ft. B", "A feat. B", "A x B")
    /// keep only the first credited artist, since most providers index
    /// the primary artist. Diacritics folded, whitespace collapsed.
    /// </summary>
    public static string CleanArtist(string artist)
    {
        var cleaned = FoldDiacritics(FirstArtist(artist));
        cleaned = CollapseWhitespace(cleaned).Trim();
        return cleaned.Length == 0 ? CollapseWhitespace(FoldDiacritics(artist)).Trim() : cleaned;
    }

    /// <summary>
    /// The single search string most providers expect: clean artist and
    /// clean title joined with one space.
    /// </summary>
    public static string Query(string artist, string title)
        => string.Join(" ", new[] { CleanArtist(artist), CleanTitle(title) }
            .Where(s => s.Length > 0));

    /// <summary>
    /// Removes "(...)" and "[...]" groups. Keeps text when the group is
    /// the entire title: "("(Original Mix)")" alone would otherwise
    /// produce an empty query.
    /// </summary>
    private static string DropParentheticals(string title)
    {
        var without = ParentheticalRegex().Replace(title, " ");
        return without.Trim().Length == 0 ? title : without;
    }

    /// <summary>Drops ft./feat./featuring/"with"/vs./x tags from a title.</summary>
    private static string DropFeaturing(string title)
    {
        var without = FeaturingRegex().Replace(title, " ");
        return without.Trim().Length == 0 ? title : without;
    }

    /// <summary>"A ft. B, A feat B, A x B" -> "A": the first credited artist.</summary>
    private static string FirstArtist(string artist)
    {
        var without = FeaturingRegex().Replace(artist, " ");
        var first = without.Split(',', 2)[0];
        return first.Trim().Length == 0 ? artist : first;
    }

    /// <summary>
    /// é -> e, ü -> u, and so on, using the platform's built-in fold:
    /// the unicode normalization that turns decomposed accents into base
    /// letters, after which the non-ASCII marks are dropped.
    /// </summary>
    private static string FoldDiacritics(string text)
    {
        if (text.Length == 0) return text;
        var folded = text.Normalize(System.Text.NormalizationForm.FormD);
        var chars = folded.Where(c =>
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return string.Concat(chars);
    }

    private static string CollapseWhitespace(string text)
        => WhitespaceRegex().Replace(text, " ");

    // Group starts with a word boundary so "mixtape" never matches.
    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[([][^)\]]*[)\]]*\s*")]
    private static partial System.Text.RegularExpressions.Regex ParentheticalRegex();

    // ft./feat./featuring/with/vs, or a standalone " x " separator. The
    // leading \s anchors it between words, so "Remix" or "Track" never
    // lose their letters.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\s+(?:ft\.?|feat\.?|featuring|with|vs\.?|x)\s+.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FeaturingRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();
}
