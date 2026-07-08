using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Background service that manages file system monitoring for library folders.
/// Automatically starts/stops watchers based on ScanLocationEntity.LiveMonitoring setting.
/// </summary>
public class FileSystemMonitoringBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FileSystemMonitoringBackgroundService> _logger;
    private IFileSystemMonitor? _monitor;

    public FileSystemMonitoringBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<FileSystemMonitoringBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Get the IFileSystemMonitor singleton
            _monitor = _serviceProvider.GetRequiredService<IFileSystemMonitor>();

            // Subscribe to changes
            _monitor.ChangeDetected += OnFileSystemChange;

            _logger.LogInformation("File system monitoring background service started");

            // Initial load of monitored paths
            await LoadMonitoredPathsAsync(stoppingToken);

            // Check for new/removed monitored paths every 30 seconds
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await LoadMonitoredPathsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing monitored paths");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File system monitoring background service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File system monitoring background service error");
        }
        finally
        {
            if (_monitor != null)
            {
                _monitor.ChangeDetected -= OnFileSystemChange;
                _monitor.StopAll();
            }
        }
    }

    private async Task LoadMonitoredPathsAsync(CancellationToken ct)
    {
        if (_monitor == null) return;

        try
        {
            using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MelodyBridgeDbContext>();

            // Get all scan locations with LiveMonitoring enabled
            var monitoredLocations = await db.ScanLocations
                .Where(sl => sl.LiveMonitoring)
                .ToListAsync(ct);

            var currentlyMonitored = _monitor.GetMonitoredPaths();

            // Start monitoring new paths
            foreach (var location in monitoredLocations)
            {
                if (!string.IsNullOrWhiteSpace(location.Path) &&
                    !currentlyMonitored.Contains(location.Path, StringComparer.OrdinalIgnoreCase))
                {
                    _monitor.StartMonitoring(location.Path, location.Id);
                }
            }

            // Stop monitoring paths that are no longer enabled
            foreach (var path in currentlyMonitored)
            {
                var isStillMonitored = monitoredLocations.Any(
                    sl => sl.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

                if (!isStillMonitored)
                {
                    _monitor.StopMonitoring(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading monitored paths");
        }
    }

    private void OnFileSystemChange(object? sender, FileSystemChangeEventArgs e)
    {
        _logger.LogDebug("File system change detected: {ChangeType} - {Path}", e.ChangeType, e.Path);

        // Queue a scan for this location
        // The ScanSchedulingBackgroundService will handle the actual scan
        // by checking if it's time for a rescan
    }
}

public class ScanLocationEntity
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public int? ScanIntervalHours { get; set; }
    public string? ScheduleCron { get; set; }
    public bool LiveMonitoring { get; set; }
    public DateTime? LastScannedAt { get; set; }
}
