namespace MelodyBridge.Server.Services;

/// <summary>The automatic repair a group gets from the one-click fixer.</summary>
public enum AttentionFixAction
{
    Retry,
    Keep,
    RetryReview,
    Reverify,
    Hardlink,
    Archive,
}

/// <summary>
/// The per-kind repair policy. Kept out of the Blazor component so the
/// mapping can be tested without a rendered page or a download.
/// </summary>
public static class AttentionFixPolicy
{
    public static AttentionFixAction ForKind(string kind) => kind switch
    {
        var failure when kind.StartsWith("failed-", StringComparison.Ordinal)
            => AttentionFixAction.Retry,
        "dedup" => AttentionFixAction.Hardlink,
        "review-length" => AttentionFixAction.Keep,
        "review-spectrum" or "review-both" => AttentionFixAction.RetryReview,
        "unverified" => AttentionFixAction.Reverify,
        "archive" => AttentionFixAction.Archive,
        "file-missing" => AttentionFixAction.Retry,
        _ => AttentionFixAction.Retry,
    };

    public static string Summary(AttentionFixAction action, int count) => action switch
    {
        AttentionFixAction.Retry =>
            $"retry {count} download{(count == 1 ? "" : "s")}",
        AttentionFixAction.Keep =>
            $"keep {count} length-reviewed file{(count == 1 ? "" : "s")}",
        AttentionFixAction.RetryReview =>
            $"retry {count} quality review{(count == 1 ? "" : "s")}",
        AttentionFixAction.Hardlink =>
            $"hardlink {count} duplicate{(count == 1 ? "" : "s")}",
        AttentionFixAction.Reverify =>
            $"recheck {count} unverified file{(count == 1 ? "" : "s")}",
        AttentionFixAction.Archive =>
            $"rebuild {count} archive cop{(count == 1 ? "y" : "ies")}",
        _ => $"handle {count} track{(count == 1 ? "" : "s")}",
    };
}
