using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Runs the update check in the background: the what's-new lookup after an
/// upgrade and a daily look for a newer release. A short delay keeps GitHub
/// off the startup path; every failure is swallowed inside
/// <see cref="UpdateService"/> so the app never depends on the network.
/// </summary>
public sealed class UpdateWatchService(
    UpdateService updates,
    ILogger<UpdateWatchService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await updates.EnsureReleaseNotesAsync(stoppingToken);
                await updates.CheckAndNotifyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A broken update check is never worth a crash loop.
                logger.LogDebug(ex, "Update watcher sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
