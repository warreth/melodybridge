using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Shared candidate ranking for plugins whose source returns several
/// search results. Instead of trusting the API's first row (usually a
/// remix, a lyric video or a live cut), every candidate is scored on
/// title/artist similarity plus duration proximity, grossly mismatched
/// lengths are dropped, and the runner-ups travel along as alternatives
/// so a dead first choice does not cost the whole plugin.
/// </summary>
public static class SearchRanker
{
    /// <summary>
    /// Picks the best candidate and attaches up to
    /// <paramref name="maxAlternatives"/> High-confidence runner-ups
    /// (distinct URLs) to it. Candidates whose length is grossly wrong
    /// lose against plausible ones; when none is plausible the best
    /// wrong-length candidate is still returned, so the caller (the
    /// waterfall) can reject it with a precise reason instead of
    /// reporting a bare miss. <paramref name="tieBreaker"/> breaks equal
    /// scores (e.g. prefer the lossless stream when two exact matches
    /// tie).
    /// </summary>
    public static DownloaderSearchHit? Rank(
        string artist, string title, TimeSpan? expectedDuration,
        IEnumerable<DownloaderSearchHit> candidates, int maxAlternatives = 2,
        Func<DownloaderSearchHit, int>? tieBreaker = null)
    {
        var scored = candidates
            .Where(c => !string.IsNullOrEmpty(c.SourceUrl))
            // Karaoke, covers and unrequested remixes/live cuts are the
            // wrong recording, not a fallback: drop them entirely.
            .Where(c => !FuzzyMatcher.AddsUnrequestedModifier(title, c.Title))
            .Select(c => (candidate: c, score: FuzzyMatcher.ScoreWithDuration(
                artist, title, expectedDuration, c.Artist, c.Title, c.Duration)))
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => tieBreaker?.Invoke(x.candidate) ?? 0)
            .ThenByDescending(x => x.candidate.MatchConfidence == MatchConfidence.High)
            .ToList();

        if (scored.Count == 0) return null;

        var ranked = scored
            .Where(x => FuzzyMatcher.DurationPlausible(expectedDuration, x.candidate.Duration))
            .ToList();
        if (ranked.Count == 0)
            return scored[0].candidate; // wrong length: the caller's gate decides

        var primary = ranked[0].candidate;
        // Alternatives are the safety net for a dead first choice, so
        // only candidates the ranker itself would vouch for ride along:
        // a Low-confidence runner-up (a cover, a karaoke channel) is the
        // wrong track, and the next plugin gets a fair chance instead.
        var alternatives = ranked
            .Skip(1)
            .Select(x => x.candidate)
            .Where(c => c.MatchConfidence == MatchConfidence.High)
            .Where(c => c.SourceUrl != primary.SourceUrl)
            .Take(maxAlternatives)
            .ToList();
        return alternatives.Count == 0 ? primary : primary with { Alternatives = alternatives };
    }
}
