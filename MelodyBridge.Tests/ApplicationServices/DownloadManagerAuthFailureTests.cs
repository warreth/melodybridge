using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// A stale login must not read as "no source had this track". The
/// waterfall reports the login reason, and the app-wide register learns
/// about it so the card and the sidebar can mark it.
/// </summary>
[TestFixture]
public class DownloadManagerAuthFailureTests
{
    private sealed class BrokenLoginDownloader : IDownloader, IPluginAuthStatusProvider
    {
        public string Id => "qobuz";
        public string Name => "Qobuz";
        public string Description => "test";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public PluginAuthStatus AuthStatus { get; } =
            new(PluginAuthState.Blocked, "Qobuz blocked this account (USER_BLOCKED)");

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "blocked"));
    }

    [Test]
    public async Task BlockedLogin_BecomesTheTrackFailure_AndTheMonitorIssue()
    {
        var monitor = new PluginAuthMonitor();
        var manager = new DownloadManager(
            new ListDownloaderRegistry(new BrokenLoginDownloader()),
            NullLogger<DownloadManager>.Instance,
            auth: monitor);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any),
            Path.GetTempPath(), "mel-auth");

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Null);
            Assert.That(manager.LastFailure("mel-auth"), Does.Contain("Qobuz blocked this account"),
                "the track row must name the login problem, not blame the catalogue");
            Assert.That(monitor.Snapshot().Single().PluginId, Is.EqualTo("qobuz"));
            Assert.That(monitor.Snapshot().Single().Message, Does.Contain("USER_BLOCKED"));
        });
    }

    [Test]
    public async Task SuccessfulProbe_ClearsAnOldMonitorIssue()
    {
        var monitor = new PluginAuthMonitor();
        monitor.Report("qobuz", "Qobuz", "The token expired");

        var downloader = new HealthyAuthDownloader();
        var manager = new DownloadManager(
            new ListDownloaderRegistry(downloader),
            NullLogger<DownloadManager>.Instance,
            auth: monitor);

        await manager.DownloadTrackAsync(
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any),
            Path.GetTempPath(), "mel-healthy");

        Assert.That(monitor.Snapshot(), Is.Empty,
            "a probe that reports a valid login clears the old red mark");
    }

    [Test]
    public async Task LastAttemptedPlugin_IsRecordedEvenWhenTheDownloadFails()
    {
        var manager = new DownloadManager(
            new ListDownloaderRegistry(new SearchingDownloader()),
            NullLogger<DownloadManager>.Instance);

        await manager.DownloadTrackAsync(
            new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any),
            Path.GetTempPath(), "mel-attempt");

        Assert.That(manager.LastAttemptedPluginId("mel-attempt"), Is.EqualTo("qobuz"),
            "a retry elsewhere needs the provider that just failed");
    }

    private sealed class SearchingDownloader : IDownloader
    {
        public string Id => "qobuz";
        public string Name => "Qobuz";
        public string Description => "test";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                "Around the World", "Daft Punk", "https://qobuz.example/1",
                TimeSpan.FromSeconds(429), MatchConfidence: MatchConfidence.High));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "dead link"));
    }

    private sealed class HealthyAuthDownloader : IDownloader, IPluginAuthStatusProvider
    {
        public string Id => "qobuz";
        public string Name => "Qobuz";
        public string Description => "test";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public PluginAuthStatus AuthStatus { get; } = PluginAuthStatus.Valid;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "miss"));
    }
}
