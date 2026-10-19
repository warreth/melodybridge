using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The app-wide login register: only a login that used to work counts as
/// a problem, a repeated report is quiet, and the event fires when the
/// thing the UI draws actually changes.
/// </summary>
[TestFixture]
public class PluginAuthMonitorTests
{
    private sealed class AuthPlugin : IDownloader, IPluginAuthStatusProvider
    {
        public AuthPlugin(string id, string name, PluginAuthStatus status)
        {
            Id = id;
            Name = name;
            AuthStatus = status;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => "test";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public PluginAuthStatus AuthStatus { get; set; }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "test"));
    }

    [Test]
    public void Observe_AnExpiredLogin_CreatesAnIssue()
    {
        var monitor = new PluginAuthMonitor();
        var plugin = new AuthPlugin("qobuz", "Qobuz",
            new PluginAuthStatus(PluginAuthState.Expired, "The token expired"));

        monitor.Observe(plugin);

        var issue = monitor.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(issue.PluginId, Is.EqualTo("qobuz"));
            Assert.That(issue.PluginName, Is.EqualTo("Qobuz"));
            Assert.That(issue.Message, Is.EqualTo("The token expired"));
            Assert.That(monitor.HasIssues, Is.True);
        });
    }

    [Test]
    public void Observe_AMissingLogin_IsNotARedProblem()
    {
        var monitor = new PluginAuthMonitor();
        monitor.Observe(new AuthPlugin("qobuz", "Qobuz",
            new PluginAuthStatus(PluginAuthState.Missing, "No token yet")));

        Assert.That(monitor.Snapshot(), Is.Empty,
            "a plugin that was never set up must not raise the login alarm");
    }

    [Test]
    public void Observe_AValidLogin_ClearsAnEarlierIssue()
    {
        var monitor = new PluginAuthMonitor();
        var plugin = new AuthPlugin("tidal", "Tidal",
            new PluginAuthStatus(PluginAuthState.Rejected, "The token was rejected"));
        monitor.Observe(plugin);
        Assert.That(monitor.HasIssues, Is.True);

        plugin.AuthStatus = PluginAuthStatus.Valid;
        monitor.Observe(plugin);

        Assert.That(monitor.Snapshot(), Is.Empty);
    }

    [Test]
    public void Report_TheSameProblem_DoesNotRaiseChangedAgain()
    {
        var monitor = new PluginAuthMonitor();
        var changes = 0;
        monitor.Changed += () => changes++;

        monitor.Report("qobuz", "Qobuz", "The token expired");
        monitor.Report("qobuz", "Qobuz", "The token expired");

        Assert.That(changes, Is.EqualTo(1), "a re-probe with the same answer is not a new event");
    }

    [Test]
    public void Report_AChangedMessage_RaisesChanged()
    {
        var monitor = new PluginAuthMonitor();
        var changes = 0;
        monitor.Changed += () => changes++;

        monitor.Report("qobuz", "Qobuz", "The token expired");
        monitor.Report("qobuz", "Qobuz", "Qobuz blocked this account");

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(monitor.Snapshot().Single().Message, Does.Contain("blocked"));
        });
    }

    [Test]
    public void Clear_RemovesTheIssue_AndRaisesChanged()
    {
        var monitor = new PluginAuthMonitor();
        var changes = 0;
        monitor.Changed += () => changes++;
        monitor.Report("qobuz", "Qobuz", "The token expired");

        monitor.Clear("qobuz");

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Snapshot(), Is.Empty);
            Assert.That(changes, Is.EqualTo(2));
        });
    }
}
