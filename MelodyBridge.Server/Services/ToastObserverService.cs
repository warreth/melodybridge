using MelodyBridge.Application.Services;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Watches the download coordinator and sync jobs, and turns finished
/// runs or failures into toasts - gated by the Advanced toggles. Polling
/// is deliberate: both sources already live in the database or a
/// singleton snapshot, so no event plumbing across layers is needed.
/// The decision rules live in <see cref="ToastDecisions"/>.
/// </summary>
public sealed class ToastObserverService(
    DownloadCoordinator coordinator,
    IDbContextFactory<MelodyBridgeDbContext> dbFactory,
    SettingsStore settings,
    NotificationService notifications,
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
                await ObserveCoordinatorAsync();
                await ObserveSyncJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let toast plumbing kill the loop.
                logger.LogDebug(ex, "Toast observer sweep failed");
            }

            await Task.Delay(Poll, stoppingToken);
        }
    }

    private async Task ObserveCoordinatorAsync()
    {
        if (!await settings.GetBoolAsync("notify_downloads", true)) return;

        foreach (var (message, level) in _decisions.ObservePlaylists(coordinator.Snapshot()))
            notifications.Background(message, level);
    }

    private async Task ObserveSyncJobsAsync(CancellationToken ct)
    {
        // Completions are a download notice; failures are their own
        // toggle. Either way the observer still has to track the jobs so
        // a later enable does not replay old runs as fresh toasts.
        var notifyCompletions = await settings.GetBoolAsync("notify_downloads", true);
        var notifyFailures = await settings.GetBoolAsync("notify_failures", true);
        if (!notifyCompletions && !notifyFailures) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.SyncJobs.AsNoTracking()
            .Select(j => new { j.Id, j.Name, j.LastRunAt, j.LastRunStatus, j.LastRunSummary })
            .ToListAsync(ct);

        var rows = jobs.Select(j => (j.Id, j.Name, j.LastRunAt, j.LastRunStatus, j.LastRunSummary));
        foreach (var (message, level) in _decisions.ObserveJobs(
                     rows, notifyCompletions, notifyFailures, _startedAtUtc))
        {
            notifications.Background(message, level);
        }
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
