using MelodyBridge.Application.Services;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Watches the download coordinator and sync jobs, and turns finished
/// runs or failures into notifications: toasts follow the Notifications
/// tab on Settings, ntfy follows its own category switches. Polling is deliberate:
/// both sources already live in the database or a singleton snapshot, so
/// no event plumbing across layers is needed. The decision rules live in
/// <see cref="ToastDecisions"/>, the toast gating in
/// <see cref="NotificationRouting"/>.
/// </summary>
public sealed class ToastObserverService(
    DownloadCoordinator coordinator,
    IDbContextFactory<MelodyBridgeDbContext> dbFactory,
    SettingsStore settings,
    NotificationService notifications,
    NtfyNotifier ntfy,
    ILogger<ToastObserverService> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    private readonly ToastDecisions _decisions = new();
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedJobStateAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ObserveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let notification plumbing kill the loop.
                logger.LogDebug(ex, "Toast observer sweep failed");
            }

            await Task.Delay(Poll, stoppingToken);
        }
    }

    /// <summary>
    /// One sweep. The decisions always run, so their state stays current
    /// (turning a toggle on later must not replay old runs); the toggles
    /// only decide which events reach the toasts, and ntfy has its own
    /// switches inside the notifier.
    /// </summary>
    private async Task ObserveAsync(CancellationToken ct)
    {
        var notifyDownloads = await settings.GetBoolAsync("notify_downloads", true, ct);
        var notifyFailures = await settings.GetBoolAsync("notify_failures", true, ct);

        foreach (var notification in _decisions.ObservePlaylists(coordinator.Snapshot()))
            await PublishAsync(notification, notifyDownloads, notifyFailures, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.SyncJobs.AsNoTracking()
            .Select(j => new { j.Id, j.Name, j.LastRunAt, j.LastRunStatus, j.LastRunSummary })
            .ToListAsync(ct);

        var rows = jobs.Select(j => (j.Id, j.Name, j.LastRunAt, (string?)j.LastRunStatus, j.LastRunSummary));
        foreach (var notification in _decisions.ObserveJobs(rows, _startedAtUtc))
            await PublishAsync(notification, notifyDownloads, notifyFailures, ct);
    }

    private async Task PublishAsync(
        ToastEvent notification, bool notifyDownloads, bool notifyFailures, CancellationToken ct)
    {
        if (NotificationRouting.ShouldToast(notification, notifyDownloads, notifyFailures))
            notifications.Background(notification.Message, notification.Level);
        await ntfy.NotifyAsync(notification.Category, notification.Message, notification.Level, ct);
    }

    private async Task SeedJobStateAsync(CancellationToken ct)
    {
        // Pretend every already-existing run happened in the past so a
        // restart never replays old completions as fresh toasts.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.SyncJobs.AsNoTracking()
            .Where(j => j.LastRunAt != null)
            .Select(j => new { j.Id, j.LastRunAt })
            .ToListAsync(ct);
        _decisions.SeedJobs(jobs.Select(j => (j.Id, j.LastRunAt)));
    }
}
