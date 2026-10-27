using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
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

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = TestHelpers.CreateInMemFactory();
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

    [Test]
    public void Page_ShowsGroupedFailuresAndPlaylistWarnings()
    {
        var cut = _ctx.Render<Attention>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No source found"));
            Assert.That(cut.Markup, Does.Contain("Broken"));
            Assert.That(cut.Markup, Does.Contain("Broken Mix"));
            Assert.That(cut.Markup, Does.Contain("Playlist syncs"));
            Assert.That(cut.Markup, Does.Contain("Partial Mix"));
            Assert.That(cut.Markup, Does.Not.Contain(">Good<"), "a clean track is not in the queue");
            Assert.That(cut.FindAll("button").Any(b => b.TextContent.Contains("Retry all")), Is.True);
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Filter_Failed_HidesThePlaylistSection()
    {
        var cut = _ctx.Render<Attention>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("No source found")),
            TimeSpan.FromSeconds(5));

        cut.FindAll("button.attention-metric").Single(b => b.TextContent.Contains("failed")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("No source found"));
            Assert.That(cut.Markup, Does.Not.Contain("Playlist syncs"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void SelectingAGroup_ShowsTheBulkBar()
    {
        var cut = _ctx.Render<Attention>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("No source found")),
            TimeSpan.FromSeconds(5));

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
