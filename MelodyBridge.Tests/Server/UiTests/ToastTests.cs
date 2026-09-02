using Bunit;
using MelodyBridge.Server.Components.Layout;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Toast lifecycle: the service auto-dismisses every pushed toast after
/// its delay, manual dismissal before the timer is a harmless no-op, and
/// the capacity cap still trims the stack. The layout test proves the
/// rendered stack actually empties in the DOM.
/// </summary>
[TestFixture]
[Category("UI")]
public class ToastTests
{
    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("condition not met within timeout");
    }

    [Test]
    public async Task Push_AutoDismisses_AfterDelay()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMilliseconds(100));
        svc.Success("Saved to disk");

        Assert.That(svc.Snapshot().Count, Is.EqualTo(1),
            "the toast is visible right after the push");
        await WaitUntil(() => svc.Snapshot().Count == 0, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Push_ThreeToasts_AllAutoDismiss()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMilliseconds(100));
        svc.Info("one");
        svc.Info("two");
        svc.Info("three");

        Assert.That(svc.Snapshot().Count, Is.EqualTo(3));
        await WaitUntil(() => svc.Snapshot().Count == 0, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ManualDismiss_BeforeTimer_NoErrorAndTimerNoOps()
    {
        // Long delay: the manual dismiss happens well before the timer.
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMilliseconds(400));
        svc.Warn("stale settings");
        var toast = svc.Snapshot().Single();
        svc.Dismiss(toast);

        Assert.That(svc.Snapshot().Count, Is.EqualTo(0),
            "manual dismissal removes the toast immediately");
        await Task.Delay(700);
        Assert.That(svc.Snapshot().Count, Is.EqualTo(0),
            "the delayed auto-dismiss finds the item gone and does nothing");
    }

    [Test]
    public void Capacity_PushBeyondCap_SnapshotCapped()
    {
        // Long delay so nothing auto-dismisses while counting.
        var svc = new NotificationService(capacity: 20, autoDismissAfter: TimeSpan.FromSeconds(10));
        for (var i = 0; i < 25; i++)
            svc.Info($"toast {i}");

        Assert.That(svc.Snapshot().Count, Is.EqualTo(20),
            "the stack keeps at most the last 20 toasts");
        Assert.That(svc.Snapshot()[0].Message, Is.EqualTo("toast 24"),
            "newest first after the trim");
    }

    [Test]
    public async Task MainLayout_ToastStack_EmptyInDom_AfterAutoDismiss()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMilliseconds(150));
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddSingleton(new DevPanelService());

        var cut = ctx.Render<MainLayout>();
        svc.Success("Download finished");

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("toast-item")), TimeSpan.FromSeconds(2));
        await WaitUntil(() => !cut.Markup.Contains("toast-item"), TimeSpan.FromSeconds(3));
    }
}
