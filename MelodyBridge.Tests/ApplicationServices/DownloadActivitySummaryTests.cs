using MelodyBridge.Application.Services;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The sidebar chip's numbers and words, kept pure so they are testable
/// without a running download coordinator.
/// </summary>
[TestFixture]
public class DownloadActivitySummaryTests
{
    private static DownloadRun Run(
        string id, int total, int done, int failed, DownloadRunState state,
        int queue = -1)
    {
        // A snapshot the coordinator would stamp carries what remains in
        // the queue; when the caller does not say, that is whatever has
        // neither been done nor failed.
        if (queue < 0) queue = total - done - failed;
        return new(id, id, total, done, failed, null, null, state, DateTime.UtcNow, queue);
    }

    [Test]
    public void TwoRuns_AddUpAndNameTheFailureCount()
    {
        var summary = DownloadActivitySummary.From(new[]
        {
            Run("one", 20, 8, 1, DownloadRunState.Running),
            Run("two", 20, 6, 1, DownloadRunState.Running),
        });

        Assert.Multiple(() =>
        {
            Assert.That(summary.Runs, Is.EqualTo(2));
            Assert.That(summary.Total, Is.EqualTo(40));
            Assert.That(summary.Done, Is.EqualTo(14));
            Assert.That(summary.Failed, Is.EqualTo(2));
            Assert.That(summary.Percent, Is.EqualTo(40));
            Assert.That(summary.Headline, Is.EqualTo("2 playlists downloading"));
            Assert.That(summary.Detail, Is.EqualTo("14/40 done · 2 failed"));
        });
    }

    [Test]
    public void AllPaused_SaysPaused()
    {
        var summary = DownloadActivitySummary.From(new[]
        {
            Run("one", 10, 4, 0, DownloadRunState.Paused),
            Run("two", 10, 2, 0, DownloadRunState.Paused),
        });

        Assert.That(summary.Headline, Is.EqualTo("2 playlists paused"));
    }

    [Test]
    public void FinishedRuns_AreNotCounted()
    {
        var summary = DownloadActivitySummary.From(new[]
        {
            Run("done", 10, 10, 0, DownloadRunState.Finished),
        });

        Assert.Multiple(() =>
        {
            Assert.That(summary.Runs, Is.EqualTo(0));
            Assert.That(summary.Headline, Is.EqualTo("No active downloads"));
            Assert.That(summary.Percent, Is.EqualTo(0));
        });
    }
}
