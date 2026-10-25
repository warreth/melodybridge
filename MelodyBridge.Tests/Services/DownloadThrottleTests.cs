using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// One slot pool for every download in the process. The limit must hold
/// across callers, not per playlist run, and a cancelled waiter must not
/// leak a slot.
/// </summary>
[TestFixture]
public class DownloadThrottleTests
{
    [Test]
    public async Task Acquire_RespectsTheLimitAcrossCallers()
    {
        var throttle = new DownloadThrottle(2);
        var first = await throttle.AcquireAsync(CancellationToken.None);
        var second = await throttle.AcquireAsync(CancellationToken.None);

        var third = throttle.AcquireAsync(CancellationToken.None);
        await Task.Delay(250);
        Assert.That(third.IsCompleted, Is.False, "a third download must wait for a slot");

        first.Dispose();
        var acquired = await third.WaitAsync(TimeSpan.FromSeconds(5));
        acquired.Dispose();
        second.Dispose();
    }

    [Test]
    public async Task CancelledWaiter_DoesNotConsumeASlot()
    {
        var throttle = new DownloadThrottle(1);
        using var held = await throttle.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await throttle.AcquireAsync(cts.Token));

        // The cancelled wait left the pool alone: giving the slot back and
        // taking it again succeeds at once.
        held.Dispose();
        using var again = await throttle.AcquireAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(throttle.Limit, Is.EqualTo(1));
    }

    [Test]
    public async Task Configure_BeforeTheNextDownload_ChangesTheLimit()
    {
        var throttle = new DownloadThrottle(2);
        throttle.Configure(1);

        using var first = await throttle.AcquireAsync(CancellationToken.None);
        var second = throttle.AcquireAsync(CancellationToken.None);
        await Task.Delay(250);
        Assert.That(second.IsCompleted, Is.False, "the new limit applies to the next download");

        first.Dispose();
        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Configure_ClampsToTheSettingsRange()
    {
        var throttle = new DownloadThrottle(2);
        throttle.Configure(99);
        Assert.That(throttle.Limit, Is.EqualTo(8));
        throttle.Configure(0);
        Assert.That(throttle.Limit, Is.EqualTo(1));
    }
}
