using System.Collections.Concurrent;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One plugin whose stored login stopped working, with the short reason
/// the user needs to see. A missing login is not an issue; only a login
/// that was set up and now fails gets an entry.
/// </summary>
public sealed record PluginAuthIssue(
    string PluginId,
    string PluginName,
    string Message,
    DateTimeOffset DetectedAtUtc);

/// <summary>
/// The app-wide register of plugin login problems. The Plugins page, the
/// download manager and the periodic auth sweep all feed it; the sidebar
/// mark, the warning banner and the toast watcher read it. Kept in memory
/// on purpose: the next real probe decides again after a restart.
/// </summary>
public sealed class PluginAuthMonitor
{
    private readonly ConcurrentDictionary<string, PluginAuthIssue> _issues =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when an issue appears, changes or clears.</summary>
    public event Action? Changed;

    /// <summary>Every current login problem, in plugin-name order.</summary>
    public IReadOnlyList<PluginAuthIssue> Snapshot() => _issues.Values
        .OrderBy(issue => issue.PluginName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool HasIssues => !_issues.IsEmpty;

    /// <summary>Reads the plugin's own verdict into the register.</summary>
    public void Observe(IDownloader plugin)
    {
        var status = (plugin as IPluginAuthStatusProvider)?.AuthStatus;
        Report(plugin.Id, plugin.Name,
            status is { NeedsAttention: true }
                ? status.Message ?? "The stored login no longer works"
                : null);
    }

    /// <summary>Sets or clears one plugin's issue. Unchanged reports are no-ops.</summary>
    public void Report(string pluginId, string pluginName, string? message)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(message))
        {
            changed = _issues.TryRemove(pluginId, out _);
        }
        else if (!_issues.TryGetValue(pluginId, out var existing)
                 || !string.Equals(existing.Message, message, StringComparison.Ordinal)
                 || !string.Equals(existing.PluginName, pluginName, StringComparison.Ordinal))
        {
            _issues[pluginId] = new PluginAuthIssue(
                pluginId, pluginName, message, DateTimeOffset.UtcNow);
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>Clears one plugin's issue, for example when it is disabled.</summary>
    public void Clear(string pluginId)
    {
        if (_issues.TryRemove(pluginId, out _))
            Changed?.Invoke();
    }
}
