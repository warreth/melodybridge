using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using MelodyBridge.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The global attention page: grouped failures, filters and the bulk bar
/// against a real database. Clicking a destructive action is left to the
/// service tests; here the page must show the right queue and controls.
/// </summary>
[TestFixture]
[Category("UI")]
public class AttentionPageTests
{
    private Bunit.TestContext _ctx = null!;
    private IDbContextFactory<MelodyBridgeDbContext> _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = TestHelpers.CreateInMemFactory();
        _factory = factory;
        _ctx.Services.AddDownloadPages(factory);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Playlists.AddRange(
            new PlaylistEntity { Id = "p1", Name = "Broken Mix", SourceUrl = "s:1" },
            new PlaylistEntity
            {
                Id = "p2",
                Name = "Partial Mix",
                SourceUrl = "s:2",
                LastSyncStatus = SyncStatus.CompletedWithWarning,
                SyncWarning = "partial fetch: got 4 of 7 tracks",
            });
        db.Tracks.AddRange(
            new TrackEntity
            {
                Id = 1,
                Title = "Broken",
                Artist = "Artist",
                MelodyId = "mel-1",
                PlaylistEntityId = "p1",
                DownloadStatus = "failed",
                DownloadError = "no source had this track",
            },
            new TrackEntity
            {
                Id = 2,
                Title = "Good",
                Artist = "Artist",
                MelodyId = "mel-2",
                PlaylistEntityId = "p1",
                DownloadStatus = "downloaded",
                CurrentPath = "/tmp/good.mp3",
            });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private static void ShowMetric(IRenderedComponent<Attention> cut, string label)
    {
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button.attention-metric")
                .Any(button => button.TextContent.Contains(label)), Is.True),
            TimeSpan.FromSeconds(5));
        cut.FindAll("button.attention-metric")
            .Single(button => button.TextContent.Contains(label))
            .Click();
    }

    private sealed class SlowActions : IAttentionActions
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AttentionBulkResult> RetryAsync(
            IReadOnlyList<int> trackIds, bool avoidLastProvider,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Task.Yield();
            progress?.Report(trackIds.Count);
            Reported.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
            return new AttentionBulkResult(trackIds.Count, 0, new List<string>());
        }

        public Task<AttentionBulkResult> HardlinkAsync(
            IReadOnlyList<int> trackIds,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new AttentionBulkResult(trackIds.Count, 0, new List<string>()));

        public Task<AttentionBulkResult> ArchiveAsync(
            IReadOnlyList<int> trackIds,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new AttentionBulkResult(trackIds.Count, 0, new List<string>()));

        public Task<AttentionBulkResult> QualityReviewAsync(
            IReadOnlyList<int> trackIds, QualityReviewAction action,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new AttentionBulkResult(trackIds.Count, 0, new List<string>()));

        public Task<AttentionBulkResult> ReverifyAsync(
            IReadOnlyList<int> trackIds,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new AttentionBulkResult(trackIds.Count, 0, new List<string>()));

        public Task<AttentionBulkResult> DedupAsync(
            IReadOnlyList<int> trackIds, bool hardlinkExisting,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new AttentionBulkResult(trackIds.Count, 0, new List<string>()));
    }

    [Test]
    public void Page_ShowsGroupedFailuresAndPlaylistWarnings()
    {
        var cut = _ctx.Render<Attention>();
        ShowMetric(cut, "failed");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No source found"));
            Assert.That(cut.Markup, Does.Contain("Broken"));
            Assert.That(cut.Markup, Does.Contain("Broken Mix"));
            Assert.That(cut.Markup, Does.Not.Contain(">Good<"), "a clean track is not in the queue");
            Assert.That(cut.FindAll("button").Any(b => b.TextContent.Contains("Fix all")), Is.True);
        }, TimeSpan.FromSeconds(5));

        ShowMetric(cut, "playlists");
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Playlist syncs"));
            Assert.That(cut.Markup, Does.Contain("Partial Mix"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void BulkAction_ShowsProgressAndStopWhileRunning()
    {
        var actions = new SlowActions();
        _ctx.Services.AddSingleton<IAttentionActions>(actions);
        var cut = _ctx.Render<Attention>();
        ShowMetric(cut, "failed");
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("No source found")),
            TimeSpan.FromSeconds(5));

        cut.Find(".attention-group-actions button").Click();

        Assert.That(actions.Started.Task.Wait(TimeSpan.FromSeconds(2)), Is.True,
            "the fake action must be the one the page invoked");
        Assert.That(actions.Reported.Task.Wait(TimeSpan.FromSeconds(2)), Is.True,
            "the fake must reach its progress report");
        var repair = _ctx.Services.GetRequiredService<IAttentionRepairRunner>();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Retrying downloads"));
            Assert.That(repair.Current, Is.Not.Null);
            Assert.That(repair.Current!.Processed, Is.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("1 of 1"));
            Assert.That(cut.FindAll("button").Any(button => button.TextContent.Contains("Stop")),
                Is.True, "a running bulk action can be stopped");
        }, TimeSpan.FromSeconds(5));

        actions.Release.TrySetResult(true);
    }

    [Test]
    public void Filter_Failed_ShowsOnlyThatCategory()
    {
        var cut = _ctx.Render<Attention>();
        ShowMetric(cut, "failed");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No source found"));
            Assert.That(cut.Markup, Does.Not.Contain("Playlist syncs"),
                "a filter shows one category, not the whole queue");
            Assert.That(cut.FindAll("button.attention-metric.active"), Has.Count.EqualTo(1));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Page_DefaultsToAutomaticFixesAndCanSwitchToManual()
    {
        var cut = _ctx.Render<Attention>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Fix what can be fixed"));
            Assert.That(cut.Markup, Does.Contain("Automatic"));
            Assert.That(cut.Markup, Does.Contain("Review individually"));
            Assert.That(cut.Markup, Does.Contain("Fix all 2"));
            Assert.That(cut.Markup, Does.Contain("retry 1 download"));
            Assert.That(cut.Markup, Does.Contain("re-sync 1 playlist"));
        }, TimeSpan.FromSeconds(5));

        ShowMetric(cut, "playlists");
        cut.WaitForAssertion(() =>
            Assert.That(cut.Find("section[aria-labelledby='group-playlists'] .attention-group-actions button")
                .TextContent, Does.Contain("Fix all 1")), TimeSpan.FromSeconds(3));

        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Review individually"))
            .Click();
        ShowMetric(cut, "failed");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll("input[aria-label='Select Broken']"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("button").Any(button => button.TextContent.Contains("Retry all")),
                Is.True);
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void SelectingAnUnverifiedRow_OffersRecheckInsteadOfArchive()
    {
        using (var db = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>().CreateDbContext())
        {
            db.Tracks.Add(new TrackEntity
            {
                Id = 3,
                Title = "Unclear",
                Artist = "Artist",
                MelodyId = "mel-3",
                PlaylistEntityId = "p1",
                DownloadStatus = "downloaded",
                CurrentPath = "/tmp/unclear.mp3",
                Warning = "could not verify the download: ffprobe is not installed",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Attention>();
        ShowMetric(cut, "archives");
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Unclear")),
            TimeSpan.FromSeconds(5));
        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Review individually"))
            .Click();
        cut.Find("input[aria-label='Select Unclear']").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll("button").Any(button => button.TextContent.Contains("Recheck files")),
                Is.True);
            Assert.That(cut.FindAll("button").Any(button => button.TextContent.Contains("Recreate archives")),
                Is.False, "an unverified file has no archive to recreate");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void GroupActions_UseTheRepairThatMatchesTheGroup()
    {
        using (var db = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>().CreateDbContext())
        {
            db.Tracks.AddRange(
                new TrackEntity
                {
                    Id = 3,
                    Title = "Unclear",
                    Artist = "Artist",
                    MelodyId = "mel-3",
                    PlaylistEntityId = "p1",
                    DownloadStatus = "downloaded",
                    CurrentPath = "/tmp/unclear.mp3",
                    Warning = "could not verify the download: ffprobe is not installed",
                },
                new TrackEntity
                {
                    Id = 4,
                    Title = "Gone",
                    Artist = "Artist",
                    MelodyId = "mel-4",
                    PlaylistEntityId = "p1",
                    DownloadStatus = "downloaded",
                    CurrentPath = "/tmp/gone.mp3",
                    Warning = "file missing on disk, will re-download",
                },
                new TrackEntity
                {
                    Id = 5,
                    Title = "No copy",
                    Artist = "Artist",
                    MelodyId = "mel-5",
                    PlaylistEntityId = "p1",
                    DownloadStatus = "downloaded",
                    CurrentPath = "/tmp/no-copy.mp3",
                    Warning = "archive copy failed: ffmpeg is not installed",
                });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Attention>();
        ShowMetric(cut, "archives");
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Gone")),
            TimeSpan.FromSeconds(5));
        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Review individually"))
            .Click();

        cut.WaitForAssertion(() =>
        {
            var missing = cut.Find("section[aria-labelledby='group-file-missing'] .attention-group-actions");
            var unverified = cut.Find("section[aria-labelledby='group-unverified'] .attention-group-actions");
            var archive = cut.Find("section[aria-labelledby='group-archive'] .attention-group-actions");
            Assert.Multiple(() =>
            {
                Assert.That(missing.TextContent, Does.Contain("Retry all"));
                Assert.That(missing.TextContent, Does.Not.Contain("Recreate all"));
                Assert.That(unverified.TextContent, Does.Contain("Recheck all"));
                Assert.That(unverified.TextContent, Does.Not.Contain("Recreate all"));
                Assert.That(archive.TextContent, Does.Contain("Recreate all"));
            });
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void SimpleMode_ShowsOneSummaryAndABigFixButton()
    {
        var factory = _factory;
        using (var db = factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = UiModeService.SettingKey,
                Value = UiModeService.Simple,
            });
            db.SaveChanges();
        }
        var settings = new SettingsStore(factory);
        _ctx.Services.AddSingleton(new InstallState(DatabaseExistedAtStartup: true));
        _ctx.Services.AddSingleton(new UiModeService(
            settings, new InstallState(DatabaseExistedAtStartup: true)));

        var cut = _ctx.Render<Attention>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Open Advanced mode"));
            Assert.That(cut.Markup, Does.Contain("Fix all 2"));
            Assert.That(cut.FindAll(".attention-summary"), Is.Empty,
                "simple mode does not show the filter bar");
            Assert.That(cut.FindAll(".attention-group"), Is.Empty,
                "simple mode does not render the queue");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void SelectingAGroup_ShowsTheBulkBar()
    {
        var cut = _ctx.Render<Attention>();
        ShowMetric(cut, "failed");
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("No source found")),
            TimeSpan.FromSeconds(5));

        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Review individually"))
            .Click();
        cut.Find("input[aria-label='Select Broken']").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("selected"));
            Assert.That(cut.Markup, Does.Contain("Retry downloads"));
            Assert.That(cut.Markup, Does.Contain("Retry elsewhere"));
            Assert.That(cut.Markup, Does.Contain("Find hardlinks"));
        }, TimeSpan.FromSeconds(3));
    }
}
