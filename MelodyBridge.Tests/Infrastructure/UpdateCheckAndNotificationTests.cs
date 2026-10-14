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

    [TestCase("1.5.0-beta1", "1.0.0", true)]       // a newer beta is newer
    [TestCase("1.0.0-beta2", "1.0.0-beta1", true)] // prerelease ordering
    [TestCase("1.0.0", "1.0.0-beta2", true)]       // a release beats its prereleases
    [TestCase("1.0.0-beta1", "1.0.0", false)]      // an older beta is not an update
    [TestCase("1.0.0-beta1", "1.5.0", false)]      // the old string-inequality bug
    [TestCase("v1.2.3", "1.2.2", true)]            // the v prefix is tolerated
    [TestCase("1.2.3+build5", "1.2.3", false)]     // build metadata is ignored
    [TestCase("1.2.3-rc.2", "1.2.3-rc.10", false)] // numeric identifiers compare numerically
    [TestCase("1.2.3-rc.1", "1.2.3-beta.1", true)] // rc sorts above beta lexically
    public void IsNewer_Orders_Semver_Correctly(string latest, string current, bool expected)
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

    [Test]
    public async Task CheckAsync_NoStableRelease_FallsBackToTheNewestPublished()
    {
        // While every release is a prerelease GitHub answers 404 for
        // /releases/latest; the About tab must still show the RC.
        var handler = new RoutingHandler(url =>
            url.Contains("/releases/latest", StringComparison.Ordinal)
                ? (System.Net.HttpStatusCode.NotFound, "{}")
                : (System.Net.HttpStatusCode.OK,
                    """
                    [
                      {"tag_name":"v1.0.0-rc1","html_url":"https://github.com/warreth/melodybridge/releases/tag/v1.0.0-rc1","draft":false,"prerelease":true},
                      {"tag_name":"1.0-beta2","html_url":"https://github.com/warreth/melodybridge/releases/tag/1.0-beta2","draft":false,"prerelease":true}
                    ]
                    """));
        var service = new UpdateCheckService(new HttpClient(handler));

        var result = await service.CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, $"error was: {result.Error}");
            Assert.That(result.LatestVersion, Is.EqualTo("1.0.0-rc1"));
            Assert.That(result.ReleaseUrl, Does.Contain("v1.0.0-rc1"));
        });
    }

    [Test]
    public async Task CheckAsync_StableReleaseExists_DoesNotFetchTheList()
    {
        var handler = new RoutingHandler(_ =>
            (System.Net.HttpStatusCode.OK,
                """{"tag_name":"v1.0.0","html_url":"https://github.com/warreth/melodybridge/releases/tag/v1.0.0"}"""));
        var service = new UpdateCheckService(new HttpClient(handler));

        var result = await service.CheckAsync();

        Assert.That(result.LatestVersion, Is.EqualTo("1.0.0"));
        Assert.That(handler.RequestedUrls.Any(u => u.Contains("/releases?", StringComparison.Ordinal)), Is.False,
            "the fallback list is only for the no-stable-release case");
    }

    [Test]
    public async Task CheckAsync_NoReleasesAtAll_KeepsTheHonestError()
    {
        var handler = new RoutingHandler(url =>
            url.Contains("/releases/latest", StringComparison.Ordinal)
                ? (System.Net.HttpStatusCode.NotFound, "{}")
                : (System.Net.HttpStatusCode.OK, "[]"));
        var service = new UpdateCheckService(new HttpClient(handler));

        var result = await service.CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("404"));
        });
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

    /// <summary>Answers per URL and records every request.</summary>
    private sealed class RoutingHandler(
        Func<string, (System.Net.HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var (status, body) = respond(url);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
