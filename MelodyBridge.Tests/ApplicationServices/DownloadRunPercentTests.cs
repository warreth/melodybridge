using MelodyBridge.Application.Services;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The run's progress bar comes from the work queue. A formula that adds
/// failures to the done count read like a full bar while the last track
/// was still downloading, and it disagreed with every page that counts
/// the queue honestly.
/// </summary>
[TestFixture]
public class DownloadRunPercentTests
{
    private static DownloadRun Run(int total, int done, int failed, int queue)
        => new("pl-1", "Playlist", total, done, failed, null, null,
            DownloadRunState.Running, DateTime.UtcNow, queue);

    [Test]
    public void NearlyDone_NeverReadsFull_WhileTheLastTrackIsQueued()
    {
        var run = Run(total: 10, done: 7, failed: 2, queue: 1);

        Assert.Multiple(() =>
        {
            Assert.That(run.Percent, Is.EqualTo(90), "one queued row keeps the bar honest");
            Assert.That(run.Percent, Is.LessThan(100));
        });
    }

    [Test]
    public void EmptyQueue_IsComplete()
    {
        Assert.That(Run(10, 8, 2, 0).Percent, Is.EqualTo(100));
    }

    [Test]
    public void EverythingQueued_IsZero()
    {
        Assert.That(Run(10, 0, 0, 10).Percent, Is.EqualTo(0));
    }

    [Test]
    public void AQueuePastTheTotal_ClampsInsteadOfGoingNegative()
    {
        Assert.That(Run(10, 0, 0, 12).Percent, Is.EqualTo(0));
    }

    [Test]
    public void NoWorkAtAll_DoesNotDivideByZero()
    {
        var run = new DownloadRun("pl-1", "Playlist", 0, 0, 0, null, null,
            DownloadRunState.Running, DateTime.UtcNow);
        Assert.That(run.Percent, Is.EqualTo(0));
    }
}
