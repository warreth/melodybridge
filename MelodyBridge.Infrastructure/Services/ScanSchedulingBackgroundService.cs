using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Background service that scans library locations when their schedule is due.
/// Schedules come from ScanLocationEntity.ScheduleCron (ScanSchedule: manual,
/// interval or cron). Legacy rows that only set ScanIntervalHours keep working
/// through a fallback conversion, so no migration is needed.
/// </summary>
public class ScanSchedulingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScanSchedulingBackgroundService> _logger;
    private readonly SettingsStore _settings;

    public ScanSchedulingBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ScanSchedulingBackgroundService> logger,
        SettingsStore settings)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scan scheduling service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pollSeconds = Math.Max(30, int.TryParse(
                    await _settings.GetAsync("auto_scan_interval", "60"), out var v) ? v : 60);
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
                var scanner = scope.ServiceProvider.GetRequiredService<ILibraryScanner>();

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var locations = await db.ScanLocations.ToListAsync(stoppingToken);

                foreach (var loc in locations)
                {
                    var lastScan = loc.LastScannedAt is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : DateTimeOffset.MinValue;
                    if (!LocationSchedule.Of(loc).IsDue(lastScan, DateTimeOffset.UtcNow))
                        continue;

                    _logger.LogInformation("Scheduled scan starting for: {path}", loc.Path);

                    try
                    {
                        await scanner.ScanAsync(new[] { new ScanLocation(loc.Path) }, stoppingToken);

                        // Re-read the row: the scanner saved through its own DbContext.
                        var fresh = await db.ScanLocations.FindAsync(new object[] { loc.Id }, stoppingToken);
                        if (fresh != null)
                        {
                            fresh.LastScannedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(stoppingToken);
                        }

                        _logger.LogInformation("Scheduled scan completed for: {path}", loc.Path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scheduled scan failed for: {path}", loc.Path);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan scheduling cycle failed");
            }
        }

        _logger.LogInformation("Scan scheduling service stopped");
    }
}

/// <summary>
/// The schedule of a location row: ScheduleCron when set, the legacy
/// ScanIntervalHours column otherwise. Kept next to its only consumer.
/// </summary>
public static class LocationSchedule
{
    public static ScanSchedule Of(ScanLocationEntity loc)
    {
        var parsed = ScanSchedule.Parse(loc.ScheduleCron);
        if (parsed.Mode != ScanScheduleMode.Manual)
            return parsed;

        // Legacy rows predate ScheduleCron; hours become an interval so old
        // configured locations keep scanning without anyone editing them.
        return loc.ScanIntervalHours is > 0
            ? ScanSchedule.FromInterval(loc.ScanIntervalHours.Value * 60)
            : ScanSchedule.Manual;
    }
}
