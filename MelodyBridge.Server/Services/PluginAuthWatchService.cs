using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Keeps the plugin login marks honest between page visits: one sweep at
/// startup, then every five minutes. Only plugins that own a token or an
/// account are probed, so the free sources are not woken up for nothing.
/// New or changed problems are pushed as an error toast; the sidebar mark
/// and the Plugins banner read the same register and stay put.
/// </summary>
public sealed class PluginAuthWatchService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(45);

    private readonly IDownloaderRegistry _registry;
    private readonly PluginAuthMonitor _monitor;
    private readonly NotificationService _notifications;
    private readonly ILogger<PluginAuthWatchService> _logger;

    // One toast per problem per session, and a new one when the reason
    // changes. The page and the download manager report the same issues;
    // recreating the same toast every 30 seconds would be noise.
    private readonly Dictionary<string, string> _toasted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _toastGate = new();

    public PluginAuthWatchService(
        IDownloaderRegistry registry,
        PluginAuthMonitor monitor,
        NotificationService notifications,
        ILogger<PluginAuthWatchService> logger)
    {
        _registry = registry;
        _monitor = monitor;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _monitor.Changed += SyncToasts;
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var timer = new PeriodicTimer(SweepInterval);
            do
            {
                await SweepAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _monitor.Changed -= SyncToasts;
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var enabled = _registry.GetEnabled();
        var enabledIds = enabled.Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A plugin the user just disabled must lose its red mark too.
        foreach (var issue in _monitor.Snapshot())
        {
            if (!enabledIds.Contains(issue.PluginId))
                _monitor.Clear(issue.PluginId);
        }

        foreach (var plugin in enabled)
        {
            if (plugin is not IPluginAuthStatusProvider) continue;

            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ProbeBudget);
                await plugin.IsAvailableAsync(budget.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Login sweep timed out for {Plugin}", plugin.Name);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Login sweep failed for {Plugin}", plugin.Name);
            }

            _monitor.Observe(plugin);
        }
    }

    /// <summary>
    /// Pushes a toast for each new or changed login problem. Internal so a
    /// test can drive it without starting the hosted service.
    /// </summary>
    internal void SyncToasts()
    {
        // Parallel downloads can report issues at the same time; one lock
        // keeps the toast de-duplication and the notification order sane.
        var fresh = new List<string>();
        lock (_toastGate)
        {
            var current = _monitor.Snapshot();
            foreach (var issue in current)
            {
                if (_toasted.TryGetValue(issue.PluginId, out var previous)
                    && string.Equals(previous, issue.Message, StringComparison.Ordinal))
                    continue;

                _toasted[issue.PluginId] = issue.Message;
                fresh.Add($"{issue.PluginName}: {issue.Message}");
            }

            foreach (var resolved in _toasted.Keys
                         .Except(current.Select(issue => issue.PluginId), StringComparer.OrdinalIgnoreCase)
                         .ToList())
                _toasted.Remove(resolved);
        }

        foreach (var message in fresh)
            _notifications.Error(message);
    }
}
