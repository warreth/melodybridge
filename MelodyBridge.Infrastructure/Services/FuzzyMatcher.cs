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
    /// True when a hit is close enough to download at all. High confidence
    /// passes; so does a near-exact title with a different or unknown
    /// uploader (label channels, "Topic" uploads, an artist name prefixed
    /// to the title), and a strongly matching artist with at least half
    /// the title. Everything else is a different recording: a miss is
    /// honest, a wrong download is not.
    /// </summary>
    public static bool IsAcceptableMatch(
        string requestedArtist, string requestedTitle,
        string? hitArtist, string? hitTitle)
    {
        if (Confidence(requestedArtist, requestedTitle, hitArtist, hitTitle) == MatchConfidence.High)
            return true;

        // Titles are often "Artist - Song" on upload platforms: compare
        // the hit against both spellings of the request and keep the
        // better one.
        var plainTitleScore = Similarity(requestedTitle, hitTitle);
        var titleScore = Math.Max(
            plainTitleScore,
            Similarity($"{requestedArtist} {requestedTitle}", hitTitle));
        if (titleScore >= 0.70) return true;

        // A clearly matching artist with a recognisable part of the
        // title is a different spelling of the same track, not a miss.
        return plainTitleScore >= 0.50
            && ArtistScore(requestedArtist, hitArtist) >= 0.85;
    }

    /// <summary>
    /// Score with a duration-proximity penalty: when the expected length
    /// is known and the hit deviates from it, equally-named candidates
    /// (a remix, a live cut, a longer album version) lose against the
    /// close match. No penalty when either duration is unknown.
    /// </summary>
    public static double ScoreWithDuration(
        string requestedArtist, string requestedTitle, TimeSpan? expectedDuration,
        string? hitArtist, string? hitTitle, TimeSpan? hitDuration)
    {
        var score = Score(requestedArtist, requestedTitle, hitArtist, hitTitle);
        if (expectedDuration is { TotalSeconds: >= 45 } expected && hitDuration is { } hit)
        {
            var tolerance = Math.Max(15, expected.TotalSeconds * 0.12);
            var delta = Math.Abs((hit - expected).TotalSeconds);
            if (delta > tolerance)
            {
                var excess = (delta - tolerance) / expected.TotalSeconds;
                score *= Math.Max(0.1, 1 - excess);
            }
        }
        return score;
    }

    /// <summary>
    /// True when a hit's length is plausible for the requested track.
    /// Both durations must be known and the expected track at least 45
    /// seconds long; the hit must land within [0.55x, 1.8x] of the
    /// expectation. Previews (a 30s clip of a 4-minute song) and
    /// unrelated extended edits are rejected; live versions with an
    /// intro still pass.
    /// </summary>
    public static bool DurationPlausible(TimeSpan? expected, TimeSpan? hit)
    {
        if (expected is not { } e || hit is not { } h) return true;
        if (e.TotalSeconds < 45) return true; // short tracks carry too little signal
        var ratio = h.TotalSeconds / e.TotalSeconds;
        return ratio is >= 0.55 and <= 1.8;
    }

    /// <summary>
    /// Titles that identify a different recording than the requested one
    /// when the request itself does not mention them: karaoke, covers,
    /// remixes, live cuts and the rest of the "wrong track" family.
    /// </summary>
    private static readonly string[] UnrequestedModifiers =
    {
        "karaoke", "cover", "tribute", "instrumental", "remix", "live",
        "acoustic", "reprise", "demo", "orchestral", "piano", "8d",
        "sped", "slowed", "nightcore", "mashup", "review", "reaction",
    };

    /// <summary>
    /// Recording markers: words in parentheses that name a specific
    /// version of a song. A hit that lacks a marker the request names is
    /// a different recording ("The Last Airbender" is not "The Last
    /// Airbender (VIP)"), not a fallback. Words a parenthesis may open
    /// with without being a marker are handled by the promotional list in
    /// SearchTerms; "version", "radio" and "edit" stay out on purpose:
    /// platforms drop them freely, and the length gate covers the
    /// differences they stand for.
    /// </summary>
    private static readonly HashSet<string> RecordingMarkers = new(StringComparer.Ordinal)
    {
        "vip", "acoustic", "live", "remix", "remaster", "remastered",
        "instrumental", "demo", "extended", "slowed", "sped", "nightcore",
        "cover", "karaoke", "piano", "orchestral", "8d", "mashup", "reprise",
    };

    /// <summary>
    /// True when the request names a specific recording, by a marker in
    /// parentheses, that the hit does not carry. This is the mirror of
    /// <see cref="AddsUnrequestedModifier"/>: one rejects extra markers,
    /// this one rejects a dropped marker.
    /// </summary>
    public static bool DropsRequestedMarker(string requestedTitle, string? hitTitle)
    {
        var markers = MarkersIn(requestedTitle);
        if (markers.Count == 0) return false;
        var hit = WordSet(hitTitle);
        foreach (var marker in markers)
            if (!hit.Contains(marker)) return true;
        return false;
    }

    /// <summary>The recording markers the title carries in parentheses or brackets.</summary>
    private static HashSet<string> MarkersIn(string? title)
    {
        var markers = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title)) return markers;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(title, @"[([][^)\]]*[)\]]"))
        {
            foreach (var token in WordSet(match.Value))
                if (RecordingMarkers.Contains(token)) markers.Add(token);
        }
        return markers;
    }

    /// <summary>
    /// True when the hit title carries a modifier the requested title
    /// does not ("… (Karaoke Version)" for a plain request). Token-based,
    /// so "Alive" never matches "live" and "Cover Me" stays itself when
    /// that is the requested title.
    /// </summary>
    public static bool AddsUnrequestedModifier(string requestedTitle, string? hitTitle)
    {
        if (string.IsNullOrWhiteSpace(hitTitle)) return false;
        var requested = WordSet(requestedTitle);
        foreach (var token in WordSet(hitTitle))
        {
            if (UnrequestedModifiers.Contains(token) && !requested.Contains(token))
                return true;
        }
        return false;
    }

    /// <summary>Lower-cased word tokens, for modifier detection only.</summary>
    private static HashSet<string> WordSet(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return words;

        var builder = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0)
            {
                words.Add(builder.ToString());
                builder.Clear();
            }
        }
        if (builder.Length > 0) words.Add(builder.ToString());
        return words;
    }

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
