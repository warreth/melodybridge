using MelodyBridge.Application.Services;
using MelodyBridge.Core;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The bounded download state behind the singleton manager: finished
/// entries age out, a count cap keeps a long-running install from growing
/// forever, in-flight entries are never dropped, and a failure stays
/// readable for a while. Time is passed in explicitly, so nothing sleeps.
/// </summary>
[TestFixture]
public class DownloadStateTests
{
    private static DownloadProgress Progress(string melodyId, string status)
        => new(melodyId, "Title", status, null, null);

    [Test]
    public void FinishedEntries_AreDroppedPastTheTtl()
    {
        var state = new DownloadState();
        var now = DateTimeOffset.UtcNow;

        state.SetProgress(Progress("old", "done"), now - DownloadState.EntryTtl - TimeSpan.FromMinutes(1));
        state.SetProgress(Progress("fresh", "done"), now);

        state.Prune(now);

        Assert.That(state.Snapshot().Select(p => p.MelodyId), Is.EqualTo(new[] { "fresh" }),
            "an old finished entry must not be remembered forever");
    }

    [Test]
    public void InFlightEntries_AreNeverDroppedByTheTtl()
    {
        var state = new DownloadState();
        var now = DateTimeOffset.UtcNow;

        // A download that has been running for an hour is still the thing
        // the UI is showing: age alone must not remove it.
        state.SetProgress(Progress("running", "downloading"), now - TimeSpan.FromHours(1));
        state.SetProgress(Progress("searching", "searching"), now - TimeSpan.FromHours(1));

        state.Prune(now);

        Assert.That(state.Snapshot().Select(p => p.MelodyId),
            Is.EquivalentTo(new[] { "running", "searching" }));
    }

    [Test]
    public void CountCap_DropsTheOldestFinishedEntriesFirst()
    {
        var state = new DownloadState();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < DownloadState.MaxEntries + 100; i++)
            state.SetProgress(Progress($"track-{i}", "done"), now.AddMilliseconds(-i));

        state.Prune(now);

        var ids = state.Snapshot().Select(p => p.MelodyId).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(ids, Has.Count.LessThanOrEqualTo(DownloadState.MaxEntries),
                "the singleton must not grow without bound");
            Assert.That(ids, Does.Contain("track-0"), "the newest entry survives");
            Assert.That(ids, Does.Not.Contain($"track-{DownloadState.MaxEntries + 99}"),
                "the oldest entries are the ones dropped");
        });
    }

    [Test]
    public void Failures_StayReadableTwice_ThenAgeOut()
    {
        var state = new DownloadState();
        var now = DateTimeOffset.UtcNow;

        state.SetFailure("mel-1", "no source had this track", now);

        state.Prune(now);
        Assert.That(state.LastFailure("mel-1"), Is.EqualTo("no source had this track"),
            "the caller may read the reason more than once");
        Assert.That(state.LastFailure("mel-1"), Is.EqualTo("no source had this track"));

        state.Prune(now + DownloadState.EntryTtl + TimeSpan.FromMinutes(1));
        Assert.That(state.LastFailure("mel-1"), Is.Null, "but not forever");
    }

    [Test]
    public void ClearFailure_RemovesTheReason()
    {
        var state = new DownloadState();
        state.SetFailure("mel-2", "old reason");
        state.ClearFailure("mel-2");

        Assert.That(state.LastFailure("mel-2"), Is.Null,
            "a fresh attempt at the same track starts without a stale reason");
    }

    [Test]
    public void CountCap_NeverDropsInFlightEntries()
    {
        var state = new DownloadState();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < DownloadState.MaxEntries + 10; i++)
            state.SetProgress(Progress($"done-{i}", "done"), now.AddMinutes(-1).AddMilliseconds(i));
        state.SetProgress(Progress("running", "downloading"), now);

        state.Prune(now);

        Assert.That(state.Snapshot().Any(p => p.MelodyId == "running"), Is.True,
            "trimming must never hide a track that is still downloading");
    }
}
