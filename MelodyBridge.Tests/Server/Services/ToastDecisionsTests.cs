using MelodyBridge.Application.Services;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The toast decision rules: every run of the same playlist toasts again
/// (keyed by its start time, not a one-shot set), sync job completions
/// follow the download toggle while failures follow the failure toggle,
/// and runs from before the process started are never replayed.
/// </summary>
[TestFixture]
public class ToastDecisionsTests
{
    private static DownloadRun Run(string playlistId, string name, DateTime startedAtUtc,
        int done = 3, int total = 3, int failed = 0)
        => new(playlistId, name, total, done, failed, null, null,
            DownloadRunState.Finished, startedAtUtc);

    private static (string Id, string Name, DateTime? LastRunAt, string? Status, string? Summary) Job(
        string id, DateTime? at, string status = "Completed", string? summary = null)
        => (id, "Job " + id, at, status, summary);

    [Test]
    public void FinishedRun_Toasts()
    {
        var decisions = new ToastDecisions();

        var toasts = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", DateTime.UtcNow) }).ToList();

        Assert.That(toasts, Has.Count.EqualTo(1));
        Assert.That(toasts[0].Message, Does.Contain("'Mix' finished: 3 of 3"));
        Assert.That(toasts[0].Level, Is.EqualTo("success"));
    }

    [Test]
    public void SecondRunOfTheSamePlaylist_ToastsAgain()
    {
        var decisions = new ToastDecisions();
        var firstStart = DateTime.UtcNow;

        decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", firstStart) }).ToList();

        // The same finished run observed twice must not duplicate.
        var repeated = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", firstStart) }).ToList();
        Assert.That(repeated, Is.Empty, "the same run is not re-toasted on the next sweep");

        // A new run of the same playlist is a new event.
        var second = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", firstStart + TimeSpan.FromMinutes(5)) }).ToList();
        Assert.That(second, Has.Count.EqualTo(1),
            "the next run of the same playlist must toast again");
    }

    [Test]
    public void FailedRun_ToastsAsWarning()
    {
        var decisions = new ToastDecisions();

        var toasts = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", DateTime.UtcNow, done: 1, failed: 2) }).ToList();

        Assert.That(toasts, Has.Count.EqualTo(1));
        Assert.That(toasts[0].Level, Is.EqualTo("warn"));
        Assert.That(toasts[0].Message, Does.Contain("2 failed"));
    }

    [Test]
    public void SyncJobCompletion_FollowsTheDownloadToggle()
    {
        var decisions = new ToastDecisions();

        var off = decisions.ObserveJobs(
            new[] { Job("j1", DateTime.UtcNow) },
            notifyCompletions: false, notifyFailures: true, startedAtUtc: DateTime.MinValue).ToList();
        Assert.That(off, Is.Empty, "completions follow the download toggle, not the failure toggle");

        var on = decisions.ObserveJobs(
            new[] { Job("j1", DateTime.UtcNow) },
            notifyCompletions: true, notifyFailures: true, startedAtUtc: DateTime.MinValue).ToList();
        Assert.That(on, Has.Count.EqualTo(1));
        Assert.That(on[0].Message, Does.Contain("completed"));
    }

    [Test]
    public void SyncJobFailure_FollowsTheFailureToggle()
    {
        var decisions = new ToastDecisions();

        var off = decisions.ObserveJobs(
            new[] { Job("j1", DateTime.UtcNow, "Failed", "boom") },
            notifyCompletions: true, notifyFailures: false, startedAtUtc: DateTime.MinValue).ToList();
        Assert.That(off, Is.Empty, "failures follow the failure toggle, not the download toggle");

        var on = decisions.ObserveJobs(
            new[] { Job("j1", DateTime.UtcNow, "Failed", "boom") },
            notifyCompletions: true, notifyFailures: true, startedAtUtc: DateTime.MinValue).ToList();
        Assert.That(on, Has.Count.EqualTo(1));
        Assert.That(on[0].Level, Is.EqualTo("warn"));
        Assert.That(on[0].Message, Does.Contain("failed"));
    }

    [Test]
    public void OldJobs_AreSeeded_AndNeverReplayed()
    {
        var decisions = new ToastDecisions();
        var old = DateTime.UtcNow - TimeSpan.FromHours(2);
        var startedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        decisions.SeedJobs(new[] { ("j1", (DateTime?)old) });

        var toasts = decisions.ObserveJobs(
            new[] { Job("j1", old) },
            notifyCompletions: true, notifyFailures: true, startedAtUtc: startedAt).ToList();

        Assert.That(toasts, Is.Empty, "a run from before the process started is not replayed");
    }

    [Test]
    public void JobFromThisSession_ToastsOnce()
    {
        var decisions = new ToastDecisions();
        var startedAt = DateTime.UtcNow;
        var ranAt = startedAt + TimeSpan.FromSeconds(30);
        var job = Job("j1", ranAt);

        var first = decisions.ObserveJobs(new[] { job }, true, true, startedAt).ToList();
        var second = decisions.ObserveJobs(new[] { job }, true, true, startedAt).ToList();

        Assert.That(first, Has.Count.EqualTo(1), "a job that ran while we watched toasts");
        Assert.That(second, Is.Empty, "the same run is not toasted twice");
    }
}
