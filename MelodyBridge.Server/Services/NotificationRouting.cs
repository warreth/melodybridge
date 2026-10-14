namespace MelodyBridge.Server.Services;

/// <summary>
/// Maps a notification category onto the toast toggles on the Advanced
/// page. Kept apart from the observer loop so the rule is directly
/// testable; ntfy has its own per-category switches and is not gated here.
/// </summary>
public static class NotificationRouting
{
    public static bool ShouldToast(ToastEvent notification, bool notifyDownloads, bool notifyFailures)
        => notification.Category switch
        {
            NotificationCategories.Downloads => notifyDownloads,
            NotificationCategories.Jobs => notifyDownloads,
            NotificationCategories.JobFailures => notifyFailures,
            NotificationCategories.Failures => notifyFailures,
            _ => false,
        };
}
