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
        svc.Info("settings saved");
        var toast = svc.Snapshot().Single();
        svc.Dismiss(toast);

        Assert.That(svc.Snapshot().Count, Is.EqualTo(0),
            "manual dismissal removes the toast immediately");
        await Task.Delay(700);
        Assert.That(svc.Snapshot().Count, Is.EqualTo(0),
            "the delayed auto-dismiss finds the item gone and does nothing");
    }

    [Test]
    public async Task ImportantEntry_KeepsItsBellRowAfterTheToastFades()
    {
        var svc = new NotificationService(
            autoDismissAfter: TimeSpan.FromMilliseconds(50),
            problemDismissAfter: TimeSpan.FromMilliseconds(250));
        svc.Important("plugin login failed", "error", "Fix plugin", "/plugins#plugin-qobuz",
            key: "auth:qobuz");

        await Task.Delay(100);
        var toast = svc.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(toast.Message, Is.EqualTo("plugin login failed"));
            Assert.That(toast.ActionLabel, Is.EqualTo("Fix plugin"));
            Assert.That(toast.ActionUrl, Is.EqualTo("/plugins#plugin-qobuz"));
        });

        await Task.Delay(250);
        Assert.That(svc.Snapshot(), Is.Empty, "a problem toast fades after its longer delay");
        Assert.That(svc.History().Single().Message, Is.EqualTo("plugin login failed"),
            "the bell keeps an important entry after its toast is gone");
    }

    [Test]
    public void NormalToasts_DoNotEnterTheBell()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        svc.Success("saved");
        svc.Error("export failed");

        Assert.Multiple(() =>
        {
            Assert.That(svc.Snapshot(), Has.Count.EqualTo(2), "both still show as toasts");
            Assert.That(svc.History(), Is.Empty,
                "routine feedback must not turn the bell into a log");
            Assert.That(svc.UnreadCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void HistoryAndUnread_CountOnlyImportantEntriesUntilMarkedSeen()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        svc.Success("one");
        svc.Important("two", "error", key: "sync:1");

        Assert.Multiple(() =>
        {
            Assert.That(svc.History(), Has.Count.EqualTo(1));
            Assert.That(svc.UnreadCount, Is.EqualTo(1));
        });

        svc.MarkAllSeen();
        Assert.That(svc.UnreadCount, Is.EqualTo(0));
        Assert.That(svc.History(), Has.Count.EqualTo(1), "seen does not delete the bell entry");
    }

    [Test]
    public void ImportantKey_ReplacesTheOldEntryWithoutDoubleUnread()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        svc.Important("token expired", "error", key: "auth:qobuz");
        svc.Important("token rejected", "error", key: "auth:qobuz");

        Assert.That(svc.History(), Has.Count.EqualTo(1));
        Assert.That(svc.History()[0].Message, Is.EqualTo("token rejected"));
        Assert.That(svc.UnreadCount, Is.EqualTo(1),
            "a changed reason is still the same condition");
    }

    [Test]
    public void Resolve_RemovesTheBellEntry()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        svc.Important("token expired", "error", key: "auth:qobuz");
        svc.Resolve("auth:qobuz");

        Assert.That(svc.History(), Is.Empty);
        Assert.That(svc.UnreadCount, Is.EqualTo(0));
    }

    [Test]
    public void DismissImportant_RemovesTheBellEntryAndToast()
    {
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        svc.Important("token expired", "error", key: "auth:qobuz");
        svc.DismissImportant(svc.History().Single());

        Assert.That(svc.History(), Is.Empty);
        Assert.That(svc.Snapshot(), Is.Empty);
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
    public void MainLayout_Bell_ShowsHistoryAndItsAction()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddPluginAuthMonitor();
        ctx.Services.AddUpdateService(TestHelpers.CreateInMemFactory());

        var cut = ctx.Render<MainLayout>();
        svc.Important("plugin login failed", "error", "Fix plugin", "/plugins#plugin-qobuz",
            key: "auth:qobuz");

        var bell = cut.Find(".notification-bell");
        Assert.That(bell.TextContent, Does.Contain("1"), "the bell shows the unread count");

        bell.Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find(".notification-panel").TextContent, Does.Contain("plugin login failed"));
            var action = cut.FindAll(".notification-action").Single();
            Assert.That(action.TextContent.Trim(), Is.EqualTo("Fix plugin"));
            Assert.That(action.GetAttribute("href"), Is.EqualTo("/plugins#plugin-qobuz"));
            Assert.That(svc.UnreadCount, Is.EqualTo(0), "opening the panel marks the history seen");
        });
    }

    [Test]
    public async Task MainLayout_ToastStack_EmptyInDom_AfterAutoDismiss()
    {
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var svc = new NotificationService(autoDismissAfter: TimeSpan.FromMilliseconds(150));
        ctx.Services.AddSingleton(svc);
        ctx.Services.AddPluginAuthMonitor();
        ctx.Services.AddUpdateService(TestHelpers.CreateInMemFactory());

        var cut = ctx.Render<MainLayout>();
        svc.Success("Download finished");

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("toast-item")), TimeSpan.FromSeconds(2));
        await WaitUntil(() => !cut.Markup.Contains("toast-item"), TimeSpan.FromSeconds(3));
    }
}
