using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The login watch service turns a new or changed plugin login problem
/// into one error toast, and stays quiet when the same verdict is probed
/// again. The red badge and banner read the same register.
/// </summary>
[TestFixture]
public class PluginAuthWatchServiceTests
{
    private static PluginAuthWatchService NewService(
        PluginAuthMonitor monitor, NotificationService notifications)
        => new(new EmptyRegistry(), monitor, notifications,
            NullLogger<PluginAuthWatchService>.Instance);

    [Test]
    public void NewProblem_ShowsOneErrorToast()
    {
        var monitor = new PluginAuthMonitor();
        var notifications = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        var service = NewService(monitor, notifications);

        monitor.Report("qobuz", "Qobuz", "The Qobuz token was rejected");
        service.SyncToasts();

        var toast = notifications.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(toast.Level, Is.EqualTo("error"));
            Assert.That(toast.Message, Does.Contain("Qobuz"));
            Assert.That(toast.Message, Does.Contain("token was rejected"));
        });
    }

    [Test]
    public void SameProblemAgain_DoesNotStackToasts()
    {
        var monitor = new PluginAuthMonitor();
        var notifications = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        var service = NewService(monitor, notifications);

        monitor.Report("qobuz", "Qobuz", "The Qobuz token was rejected");
        service.SyncToasts();
        monitor.Report("qobuz", "Qobuz", "The Qobuz token was rejected");
        service.SyncToasts();

        Assert.That(notifications.Snapshot(), Has.Count.EqualTo(1),
            "a repeated probe must not recreate the same toast");
    }

    [Test]
    public void ChangedProblem_ShowsTheNewReason()
    {
        var monitor = new PluginAuthMonitor();
        var notifications = new NotificationService(autoDismissAfter: TimeSpan.FromMinutes(1));
        var service = NewService(monitor, notifications);

        monitor.Report("qobuz", "Qobuz", "The Qobuz token was rejected");
        service.SyncToasts();
        monitor.Report("qobuz", "Qobuz", "Qobuz blocked this account (USER_BLOCKED)");
        service.SyncToasts();

        Assert.That(notifications.Snapshot(), Has.Count.EqualTo(2));
        Assert.That(notifications.Snapshot()[0].Message, Does.Contain("USER_BLOCKED"));
    }
}
