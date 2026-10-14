using MelodyBridge.Application.Services;

namespace MelodyBridge.Server.Services;

/// <summary>
/// The notification decisions of the toast observer, kept apart from its
/// polling loop so they are directly testable. Rules:
///  - one event per playlist run, keyed by the run's start time (a second
///    run of the same playlist fires again);
///  - every event carries its category; the toast toggles are applied by
///    <see cref="NotificationRouting"/> and the ntfy switches by
///    <see cref="NtfyNotifier"/>, so the two sinks stay independent;
///  - runs and jobs that happened before the process started are seeded
///    once and never replayed.
/// </summary>
internal sealed class ToastDecisions
{
    private readonly Dictionary<string, DateTime> _lastPlaylistRun = new();
    private readonly Dictionary<string, DateTime> _lastJobRuns = new();

    /// <summary>Records the jobs that already ran before this process started.</summary>
    public void SeedJobs(IEnumerable<(string Id, DateTime? LastRunAt)> jobs)
    {
        foreach (var job in jobs)
            if (job.LastRunAt is { } at)
                _lastJobRuns[job.Id] = at;
    }

    /// <summary>The events for the finished playlist runs, newest state wins.</summary>
    public IEnumerable<ToastEvent> ObservePlaylists(IEnumerable<DownloadRun> runs)
    {
        foreach (var run in runs)
        {
            if (run.State != DownloadRunState.Finished) continue;
            if (_lastPlaylistRun.TryGetValue(run.PlaylistId, out var seen) && seen >= run.StartedAtUtc)
                continue;
            _lastPlaylistRun[run.PlaylistId] = run.StartedAtUtc;

            var failed = run.Failed > 0 ? $" ({run.Failed} failed)" : string.Empty;
            yield return new ToastEvent(NotificationCategories.Downloads,
                $"'{run.PlaylistName}' finished: {run.Done} of {run.Total} done{failed}.",
                run.Failed > 0 ? "warn" : "success");
        }
    }

    /// <summary>The events for sync jobs that finished since the last sweep.</summary>
    public IEnumerable<ToastEvent> ObserveJobs(
        IEnumerable<(string Id, string Name, DateTime? LastRunAt, string? Status, string? Summary)> jobs,
        DateTime startedAtUtc)
    {
        foreach (var job in jobs)
        {
            if (job.LastRunAt is not { } at) continue;

            if (_lastJobRuns.TryGetValue(job.Id, out var seen))
            {
                if (seen >= at) continue;
                _lastJobRuns[job.Id] = at;
            }
            else
            {
                // A job never seen before: only report when it ran after we
                // started watching (it appeared mid-session).
                _lastJobRuns[job.Id] = at;
                if (at <= startedAtUtc) continue;
            }

            if (job.Status == "Completed")
            {
                yield return new ToastEvent(NotificationCategories.Jobs,
                    $"Sync job '{job.Name}' completed.", "success");
            }
            else
            {
                yield return new ToastEvent(NotificationCategories.JobFailures,
                    $"Sync job '{job.Name}' {job.Status?.ToLowerInvariant()}: {job.Summary}", "warn");
            }
        }
    }
}
