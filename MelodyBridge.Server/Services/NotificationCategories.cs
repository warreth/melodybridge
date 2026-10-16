namespace MelodyBridge.Server.Services;

/// <summary>
/// The notification categories the Notifications tab on Settings exposes
/// for ntfy. Toast toggles stay separate: a category can be pushed
/// without popping up and the other way around.
/// </summary>
public static class NotificationCategories
{
    /// <summary>A playlist download run finished.</summary>
    public const string Downloads = "downloads";

    /// <summary>A sync job completed.</summary>
    public const string Jobs = "jobs";

    /// <summary>A sync job failed.</summary>
    public const string JobFailures = "job_failures";

    /// <summary>Another serious background failure: auto-sync, scan, a crashed run.</summary>
    public const string Failures = "failures";
}
