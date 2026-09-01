using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Update comparison logic and the toast bus: pure behaviour, no network,
/// no fakes beyond a scripted HttpClient.
/// </summary>
[TestFixture]
public class UpdateCheckAndNotificationTests
{
    // ── Version comparison ──────────────────────────────────────

    [TestCase("1.1.0", "1.0.0", true)]
    [TestCase("1.0.0", "1.0.0", false)]
    [TestCase("0.9.9", "1.0.0", false)]
    [TestCase("2.0", "1.9.9", true)]
    [TestCase("nightly", "1.0.0", true)]
    public void IsNewer_Compares_Versions(string latest, string current, bool expected)
        => Assert.That(UpdateCheckService.IsNewer(latest, current), Is.EqualTo(expected));

    [Test]
    public async Task CheckAsync_Parses_A_Real_GitHub_Response()
    {
        // A scripted handler stands in for GitHub's API - the payload
        // shape is the real /releases/latest body.
        var handler = new StubHandler("""
            { "tag_name": "v9.9.9", "html_url": "https://github.com/warreth/melodybridge/releases/tag/v9.9.9" }
            """);
        var service = new UpdateCheckService(new HttpClient(handler));

        var result = await service.CheckAsync();

        Assert.That(result.Succeeded, Is.True, $"error was: {result.Error}");
        Assert.That(result.LatestVersion, Is.EqualTo("9.9.9"));
        Assert.That(result.ReleaseUrl, Does.Contain("v9.9.9"));
        Assert.That(UpdateCheckService.IsNewer(result.LatestVersion!, "1.0.0"), Is.True);
    }

    [Test]
    public async Task CheckAsync_Offline_Returns_Failure_Not_Throw()
    {
        var handler = new ThrowingHandler();
        var service = new UpdateCheckService(new HttpClient(handler));

        var result = await service.CheckAsync();

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
    }

    // ── Toast bus ─────────────────────────────────────────────────

    [Test]
    public void Notifications_Are_LIFO_And_Capped()
    {
        var bus = new NotificationService(capacity: 3);
        bus.Info("one");
        bus.Info("two");
        bus.Info("three");
        bus.Info("four");

        var snapshot = bus.Snapshot();
        Assert.That(snapshot.Count, Is.EqualTo(3), "capacity is enforced");
        Assert.That(snapshot[0].Message, Is.EqualTo("four"), "newest first");
    }

    [Test]
    public void Dismiss_Removes_Only_That_Toast()
    {
        var bus = new NotificationService();
        bus.Success("keep");
        bus.Error("drop");
        var drop = bus.Snapshot().First(t => t.Message == "drop");

        bus.Dismiss(drop);

        var left = bus.Snapshot();
        Assert.That(left.Count, Is.EqualTo(1));
        Assert.That(left[0].Message, Is.EqualTo("keep"));
    }

    [Test]
    public void Changed_Event_Fires_On_Every_Push()
    {
        var bus = new NotificationService();
        var fired = 0;
        bus.Changed += () => fired++;
        bus.Info("a");
        bus.Warn("b");
        Assert.That(fired, Is.EqualTo(2));
    }

    private sealed class StubHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("offline");
    }
}
