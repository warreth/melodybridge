using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Background service that periodically syncs playlists whose auto-sync
/// interval has elapsed (per-playlist intervals via PlaylistStore).
/// </summary>
public class AutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutoSyncBackgroundService> _logger;

    // Check frequently; each playlist's own interval decides what is due.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

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
                await Task.Delay(PollInterval, stoppingToken);

                using var scope = _serviceProvider.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<PlaylistStore>();
                var due = await store.GetDueForAutoSyncAsync(stoppingToken);

                foreach (var playlist in due)
                {
                    try
                    {
                        _logger.LogInformation("Auto-syncing playlist {Playlist} ({Source})",
                            playlist.Name, playlist.SourceUrl);
                        await store.AddOrRefreshAsync(playlist.SourceUrl, playlist.TargetDirectory, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-sync failed for playlist {Playlist}", playlist.Name);
                    }
                }
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
