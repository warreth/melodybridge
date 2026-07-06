using MelodyBridge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Background service that periodically checks all music sources with auto-sync enabled
/// and downloads any new tracks found.
/// </summary>
public class AutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoSyncBackgroundService> _logger;

    public AutoSyncBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<AutoSyncBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoSync background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var sourceManager = scope.ServiceProvider.GetRequiredService<IMusicSourceManager>();
                await sourceManager.AutoSyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutoSync cycle failed");
            }
        }

        _logger.LogInformation("AutoSync background service stopped");
    }
}
