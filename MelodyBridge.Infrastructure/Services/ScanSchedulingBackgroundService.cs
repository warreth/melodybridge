using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Background service that periodically scans music library locations.
/// Supports configurable intervals per location.
/// </summary>
public class ScanSchedulingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScanSchedulingBackgroundService> _logger;

    public ScanSchedulingBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ScanSchedulingBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scan scheduling service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
                var scanner = scope.ServiceProvider.GetRequiredService<ILibraryScanner>();

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var locations = await db.ScanLocations.ToListAsync(stoppingToken);

                foreach (var loc in locations)
                {
                    if (loc.ScanIntervalHours == null || loc.ScanIntervalHours <= 0)
                        continue;

                    var sinceLastScan = DateTime.UtcNow - (loc.LastScannedAt ?? DateTime.MinValue);
                    if (sinceLastScan.TotalHours < loc.ScanIntervalHours)
                        continue;

                    _logger.LogInformation("Scheduled scan starting for: {path}", loc.Path);

                    try
                    {
                        await scanner.ScanAsync(new[] { new ScanLocation(loc.Path) }, stoppingToken);

                        loc.LastScannedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);

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
