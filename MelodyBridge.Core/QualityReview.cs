namespace MelodyBridge.Core;

/// <summary>
/// How the user settles a flagged quality review on a downloaded track.
/// </summary>
public enum QualityReviewAction
{
    /// <summary>Keep the file as it is and clear the flag ("I don't mind").</summary>
    Keep,
    /// <summary>Download again, avoiding the plugin that produced the flagged file.</summary>
    Retry,
}

/// <summary>
/// The verified quality doubts a downloaded file can carry. A track row
/// stores the pending kinds joined with "+" (<see cref="Join"/>), so one
/// file that is both too short and inflated is one review with both
/// reasons, and the user settles them in one decision.
/// </summary>
public static class TrackReview
{
    /// <summary>The file's real length is outside the allowed tolerance.</summary>
    public const string Length = "length";

    /// <summary>The file's bandwidth says the declared bitrate is inflated.</summary>
    public const string Spectrum = "spectrum";

    /// <summary>True when the stored kinds include the given kind.</summary>
    public static bool Has(string? reviewKind, string kind)
        => reviewKind?.Split('+', StringSplitOptions.RemoveEmptyEntries)
               .Contains(kind, StringComparer.Ordinal) == true;

    /// <summary>The stored value for a set of kinds, stable order and no duplicates.</summary>
    public static string Join(IEnumerable<string> kinds)
        => string.Join("+", kinds.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal));
}
