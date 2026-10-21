using Bunit;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Server.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The downloads overview: an empty page points at the manual download
/// page, a live run shows its progress and controls, and the track list
/// separates active work from the recent done and failed entries.
/// </summary>
[TestFixture]
[Category("UI")]
public class DownloadsPageTests
{
    private Bunit.TestContext _ctx = null!;
    private StubCoordinator _coordinator = null!;

    private sealed class StubCoordinator : IDownloadCoordinator
    {
        public List<DownloadRun> Runs { get; } = new();
        public List<string> Actions { get; } = new();

        public IReadOnlyList<DownloadRun> Snapshot() => Runs;

        public DownloadRun? RunFor(string playlistId)
            => Runs.FirstOrDefault(run => run.PlaylistId == playlistId);

        public bool IsActive(string playlistId)
            => RunFor(playlistId) is { State: not DownloadRunState.Finished };

        public Task<bool> HasPendingWorkAsync(string playlistId, CancellationToken ct = default)
            => Task.FromResult(false);

        public void Start(string playlistId) => Actions.Add($"start:{playlistId}");

        public void Pause(string playlistId) => Actions.Add($"pause:{playlistId}");

        public void Cancel(string playlistId) => Actions.Add($"cancel:{playlistId}");
    }

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _coordinator = new StubCoordinator();
        _ctx.Services.AddSingleton<IDownloadCoordinator>(_coordinator);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private IRenderedComponent<Downloads> RenderPage(params DownloadProgress[] progress)
    {
        var manager = new Mock<IDownloadManager>();
        manager.Setup(m => m.SnapshotProgress()).Returns(progress);
        _ctx.Services.AddSingleton(manager.Object);
        return _ctx.Render<Downloads>();
    }

    [Test]
    public void EmptyPage_PointsAtManualDownload()
    {
        var cut = RenderPage();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No downloads yet"));
            Assert.That(cut.FindAll("a[href='/download']"), Is.Not.Empty,
                "the manual page is one click away");
            Assert.That(cut.FindAll(".stat-card"), Is.Empty,
                "no metrics when there is nothing to count");
        });
    }

    [Test]
    public void RunningRun_ShowsProgressAndPauses()
    {
        _coordinator.Runs.Add(new DownloadRun(
            PlaylistId: "pl-1",
            PlaylistName: "Road trip",
            Total: 10,
            Done: 4,
            Failed: 1,
            CurrentTrack: "Highway Song",
            CurrentPlugin: "Plugin A",
            State: DownloadRunState.Running,
            StartedAtUtc: DateTime.UtcNow.AddMinutes(-2),
            QueueLength: 5,
            EtaUtc: DateTime.UtcNow.AddMinutes(3)));

        var cut = RenderPage();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Road trip"));
            Assert.That(cut.Markup, Does.Contain("4 of 10 done"));
            Assert.That(cut.Markup, Does.Contain("1 failed"));
            Assert.That(cut.Markup, Does.Contain("5 queued"));
            Assert.That(cut.Markup, Does.Contain("Highway Song"));
            Assert.That(cut.Markup, Does.Contain("via Plugin A"));
            Assert.That(cut.Markup, Does.Contain("ETA "));
            Assert.That(cut.Markup, Does.Contain("50%"),
                "the bar counts done and failed tracks");
        });

        var pause = cut.FindAll("button").Single(button => button.TextContent.Contains("Pause"));
        pause.Click();

        Assert.That(_coordinator.Actions, Is.EqualTo(new[] { "pause:pl-1" }));
    }

    [Test]
    public void PausedRun_OffersResumeAndCancel()
    {
        _coordinator.Runs.Add(new DownloadRun(
            "pl-2", "Chill", 6, 2, 0, null, null,
            DownloadRunState.Paused, DateTime.UtcNow.AddMinutes(-5), QueueLength: 4));

        var cut = RenderPage();

        cut.FindAll("button").Single(button => button.TextContent.Contains("Resume")).Click();
        cut.FindAll("button").Single(button => button.TextContent.Contains("Cancel")).Click();

        Assert.That(_coordinator.Actions, Is.EqualTo(new[] { "start:pl-2", "cancel:pl-2" }));
    }

    [Test]
    public void TrackList_ShowsActiveDoneAndFailedEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var cut = RenderPage(
            new DownloadProgress("m1", "Searching song", "searching", null, null,
                At: now),
            new DownloadProgress("m2", "Done song", "done", "Plugin A",
                "/music/Artist - Done song.mp3", At: now.AddMinutes(-1)),
            new DownloadProgress("m3", "Failed song", "failed", "Plugin B", null,
                Warning: "the link expired", At: now.AddMinutes(-2)));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Searching song"));
            Assert.That(cut.Markup, Does.Contain("Searching every plugin"));
            Assert.That(cut.Markup, Does.Contain("Done song"));
            Assert.That(cut.Markup, Does.Contain("Saved"));
            Assert.That(cut.Markup, Does.Contain("/music/Artist - Done song.mp3"));
            Assert.That(cut.Markup, Does.Contain("the link expired"));
            Assert.That(cut.Markup, Does.Contain("Failed song"));
            Assert.That(cut.FindAll(".downloads-track"), Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void StatCards_CountRunsAndTracks()
    {
        _coordinator.Runs.Add(new DownloadRun(
            "pl-1", "One", 3, 1, 0, null, null,
            DownloadRunState.Running, DateTime.UtcNow));

        var cut = RenderPage(
            new DownloadProgress("m1", "Active", "downloading", "Plugin", null,
                At: DateTimeOffset.UtcNow),
            new DownloadProgress("m2", "Done", "done", "Plugin", "/music/done.mp3",
                At: DateTimeOffset.UtcNow),
            new DownloadProgress("m3", "Broken", "failed", "Plugin", null,
                At: DateTimeOffset.UtcNow));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Downloading now"));
            Assert.That(cut.Markup, Does.Contain("Finished"));
            Assert.That(cut.Markup, Does.Contain("Failed"));
            Assert.That(cut.Markup, Does.Contain("track being fetched"));
        });
    }
}
