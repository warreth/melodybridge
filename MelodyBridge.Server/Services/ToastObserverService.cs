using MelodyBridge.Application.Services;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Watches the download coordinator and sync jobs, and turns finished
/// runs or failures into toasts - gated by the Advanced toggles.
/// Polling is deliberate: both sources already live in the database or
/// a singleton snapshot, so no event plumbing across layers is needed.
/// </summary>
public sealed class ToastObserverService(
    DownloadCoordinator coordinator,
    IDbContextFactory<MelodyBridgeDbContext> dbFactory,
    SettingsStore settings,
    NotificationService notifications,
    ILogger<ToastObserverService> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    private readonly HashSet<string> _seenPlaylistRuns = [];
    private Dictionary<string, DateTime> _lastJobRuns = new();
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

        foreach (var run in coordinator.Snapshot())
        {
            if (run.State != DownloadRunState.Finished) continue;

            // First sight of a finished run is the moment to toast.
            if (!_seenPlaylistRuns.Add(run.PlaylistId)) continue;

            var level = run.Failed > 0 ? "warn" : "success";
            var failed = run.Failed > 0 ? $" ({run.Failed} failed)" : string.Empty;
            notifications.Background(
                $"'{run.PlaylistName}' finished: {run.Done} of {run.Total} done{failed}.",
                level);
        }
    }

    private async Task ObserveSyncJobsAsync(CancellationToken ct)
    {
        if (!await settings.GetBoolAsync("notify_failures", true)) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.SyncJobs.AsNoTracking()
            .Select(j => new { j.Id, j.Name, j.LastRunAt, j.LastRunStatus, j.LastRunSummary })
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            if (job.LastRunAt is not { } at) continue;

            // Known job with an old timestamp: nothing happened.
            if (_lastJobRuns.TryGetValue(job.Id, out var seen))
            {
                if (seen >= at) continue;
                _lastJobRuns[job.Id] = at;
            }
            else
            {
                // A job never seen before: only toast when it ran after
                // we started watching (it appeared mid-session).
                _lastJobRuns[job.Id] = at;
                if (at <= _startedAtUtc) continue;
            }

            if (job.LastRunStatus == "Completed")
                notifications.Background($"Sync job '{job.Name}' completed.");
            else
                notifications.Background(
                    $"Sync job '{job.Name}' {job.LastRunStatus?.ToLowerInvariant()}: {job.LastRunSummary}",
                    "warn");
        }
    }

    private async Task SeedJobStateAsync(CancellationToken ct)
    {
        // Pretend every already-existing run happened in the past so a
        // restart never replays old completions as fresh toasts.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        _lastJobRuns = await db.SyncJobs.AsNoTracking()
            .Where(j => j.LastRunAt != null)
            .ToDictionaryAsync(j => j.Id, j => j.LastRunAt!.Value, ct);
    }
}
