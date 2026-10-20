using MelodyBridge.Application.Services;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The notification decision rules: every run of the same playlist fires
/// again (keyed by its start time, not a one-shot set), every event
/// carries its category so the toast toggles and the ntfy switches can
/// gate independently, and runs from before the process started are never
/// replayed.
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
    public void FinishedRun_FiresADownloadsEvent()
    {
        var decisions = new ToastDecisions();

        var events = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", DateTime.UtcNow) }).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Category, Is.EqualTo(NotificationCategories.Downloads));
            Assert.That(events[0].Message, Does.Contain("'Mix' finished: 3 of 3"));
            Assert.That(events[0].Level, Is.EqualTo("success"));
            Assert.That(events[0].ActionLabel, Is.EqualTo("Open playlist"));
            Assert.That(events[0].ActionUrl, Is.EqualTo("/playlists/pl-1"));
        });
    }

    [Test]
    public void SecondRunOfTheSamePlaylist_FiresAgain()
    {
        var decisions = new ToastDecisions();
        var firstStart = DateTime.UtcNow;

        decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", firstStart) }).ToList();

        // The same finished run observed twice must not duplicate.
        var repeated = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", firstStart) }).ToList();
        Assert.That(repeated, Is.Empty, "the same run is not re-reported on the next sweep");

        // A new run of the same playlist is a new event.
        var second = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", firstStart + TimeSpan.FromMinutes(5)) }).ToList();
        Assert.That(second, Has.Count.EqualTo(1),
            "the next run of the same playlist must fire again");
    }

    [Test]
    public void FailedRun_IsAWarning()
    {
        var decisions = new ToastDecisions();

        var events = decisions.ObservePlaylists(new[] { Run("pl-1", "Mix", DateTime.UtcNow, done: 1, failed: 2) }).ToList();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].Level, Is.EqualTo("warn"));
        Assert.That(events[0].Message, Does.Contain("2 failed"));
        Assert.That(events[0].ActionLabel, Is.EqualTo("Review failed"));
        Assert.That(events[0].ActionUrl, Is.EqualTo("/playlists/pl-1"));
    }

    [Test]
    public void SyncJobCompletion_FiresAJobsEvent()
    {
        var decisions = new ToastDecisions();

        var events = decisions.ObserveJobs(
            new[] { Job("j1", DateTime.UtcNow) }, startedAtUtc: DateTime.MinValue).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Category, Is.EqualTo(NotificationCategories.Jobs));
            Assert.That(events[0].Message, Does.Contain("completed"));
        });
    }

    [Test]
    public void SyncJobFailure_FiresAJobFailuresEvent()
    {
        var decisions = new ToastDecisions();

        var events = decisions.ObserveJobs(
            new[] { Job("j1", DateTime.UtcNow, "Failed", "boom") }, startedAtUtc: DateTime.MinValue).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Category, Is.EqualTo(NotificationCategories.JobFailures));
            Assert.That(events[0].Level, Is.EqualTo("warn"));
            Assert.That(events[0].Message, Does.Contain("failed"));
        });
    }

    [Test]
    public void OldJobs_AreSeeded_AndNeverReplayed()
    {
        var decisions = new ToastDecisions();
        var old = DateTime.UtcNow - TimeSpan.FromHours(2);
        var startedAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        decisions.SeedJobs(new[] { ("j1", (DateTime?)old) });

        var events = decisions.ObserveJobs(
            new[] { Job("j1", old) }, startedAtUtc: startedAt).ToList();

        Assert.That(events, Is.Empty, "a run from before the process started is not replayed");
    }

    [Test]
    public void JobFromThisSession_FiresOnce()
    {
        var decisions = new ToastDecisions();
        var startedAt = DateTime.UtcNow;
        var ranAt = startedAt + TimeSpan.FromSeconds(30);
        var job = Job("j1", ranAt);

        var first = decisions.ObserveJobs(new[] { job }, startedAt).ToList();
        var second = decisions.ObserveJobs(new[] { job }, startedAt).ToList();

        Assert.That(first, Has.Count.EqualTo(1), "a job that ran while we watched fires");
        Assert.That(second, Is.Empty, "the same run is not reported twice");
    }

    // ── Toast gating (independent of ntfy) ────────────────────────

    [Test]
    public void ToastRouting_FollowsTheAdvancedToggles()
    {
        var downloads = new ToastEvent(NotificationCategories.Downloads, "m", "success");
        var completion = new ToastEvent(NotificationCategories.Jobs, "m", "success");
        var jobFailure = new ToastEvent(NotificationCategories.JobFailures, "m", "warn");
        var serious = new ToastEvent(NotificationCategories.Failures, "m", "warn");

        Assert.Multiple(() =>
        {
            Assert.That(NotificationRouting.ShouldToast(downloads, notifyDownloads: false, notifyFailures: true),
                Is.False, "completions follow the download toggle");
            Assert.That(NotificationRouting.ShouldToast(downloads, notifyDownloads: true, notifyFailures: false),
                Is.True);
            Assert.That(NotificationRouting.ShouldToast(completion, notifyDownloads: false, notifyFailures: true),
                Is.False, "sync job completions follow the download toggle");
            Assert.That(NotificationRouting.ShouldToast(jobFailure, notifyDownloads: true, notifyFailures: false),
                Is.False, "failures follow the failure toggle");
            Assert.That(NotificationRouting.ShouldToast(jobFailure, notifyDownloads: false, notifyFailures: true),
                Is.True);
            Assert.That(NotificationRouting.ShouldToast(serious, notifyDownloads: false, notifyFailures: false),
                Is.False, "background failures honour the failure toggle too");
            Assert.That(NotificationRouting.ShouldToast(serious, notifyDownloads: false, notifyFailures: true),
                Is.True);
        });
    }
}
