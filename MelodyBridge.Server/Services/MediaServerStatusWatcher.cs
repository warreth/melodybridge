using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Keeps the media server verdict warm. The dashboard shows the last
/// answer and never waits for a server that does not answer, so something
/// has to ask the servers: this service does, in the background, from the
/// first minute after the start. A page load then finds an answer waiting
/// instead of a connection attempt of its own.
/// </summary>
public sealed class MediaServerStatusWatcher(
    IMediaServerStatusProbe probe,
    ILogger<MediaServerStatusWatcher> logger) : BackgroundService
{
    /// <summary>How often the servers are asked. A status light does not
    /// need to be fresh to the second, and a server that is offline
    /// should not be poked every half minute for the life of the
    /// process. A minute is slow enough to be gentle and fast enough to
    /// notice a server that came back.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>The app is still starting up in the first seconds, and a
    /// media server on another machine may not be up yet either.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // The probe keeps its own short cache, so this only
                    // asks the servers again when the answer went stale.
                    await probe.SnapshotAsync(ct: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "The media server probe failed");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
