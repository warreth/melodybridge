using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Turns a serious background failure into the same notifications the
/// toast observer produces: a toast gated by the failure toggle, and an
/// ntfy push gated by the "failures" category. Called from catch blocks
/// in background loops, so it never throws.
/// </summary>
public sealed class BackgroundFailureSink(
    SettingsStore settings,
    NotificationService notifications,
    NtfyNotifier ntfy,
    ILogger<BackgroundFailureSink> logger) : IBackgroundFailureSink
{
    public async Task ReportAsync(string message)
    {
        try
        {
            if (await settings.GetBoolAsync("notify_failures", true))
                notifications.Background(message, "warn", "View logs", "/logs");
            await ntfy.NotifyAsync(NotificationCategories.Failures, message, "warn");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failure notification failed");
        }
    }
}
