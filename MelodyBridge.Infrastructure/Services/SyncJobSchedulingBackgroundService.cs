using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Background service that runs sync jobs whose schedule is due. A
/// one-minute poll like AutoSyncBackgroundService; the per-job schedule
/// (preset or cron) decides what runs, jobs run one after another, and a
/// failing job never stops the others. Jobs only run while the app is
/// running: there is no separate daemon.
/// </summary>
public class SyncJobSchedulingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyncJobSchedulingBackgroundService> _logger;

    /// <summary>Check cadence; each job's own schedule decides what is due.</summary>
    internal virtual TimeSpan PollInterval => TimeSpan.FromMinutes(1);

    public SyncJobSchedulingBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<SyncJobSchedulingBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Sync job scheduling service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var dbFactory = scope.ServiceProvider
                    .GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
                var runner = scope.ServiceProvider.GetRequiredService<ISyncJobRunner>();

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var jobs = await db.SyncJobs.AsNoTracking().ToListAsync(stoppingToken);

                foreach (var job in jobs)
                {
                    var schedule = SyncJobMapping.ScheduleOf(job);
                    if (schedule.Mode == Core.ScanScheduleMode.Manual)
                        continue;

                    var lastRun = job.LastRunAt is { } at
                        ? new DateTimeOffset(at, TimeSpan.Zero)
                        : DateTimeOffset.MinValue;
                    if (!schedule.IsDue(lastRun, DateTimeOffset.UtcNow))
                        continue;

                    try
                    {
                        _logger.LogInformation("Scheduled sync job '{Job}' starting", job.Name);
                        await runner.RunJobAsync(SyncJobMapping.MapToDomain(job), stoppingToken);
                        _logger.LogInformation("Scheduled sync job '{Job}' finished", job.Name);
                    }
                    catch (Exception ex)
                    {
                        // One broken job must not stop the others.
                        _logger.LogError(ex, "Scheduled sync job '{Job}' failed", job.Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync job schedule cycle failed");
            }
        }

        _logger.LogInformation("Sync job scheduling service stopped");
    }
}
