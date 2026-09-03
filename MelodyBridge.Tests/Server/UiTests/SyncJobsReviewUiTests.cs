using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Step 5 review: a playlist source must show the playlist name and its
/// platform ("My Summer Hits (Spotify)"), never the raw GUID. Also covers
/// the editing header (step title + "Editing" subtitle).
/// </summary>
[TestFixture]
[Category("UI")]
public class SyncJobsReviewUiTests
{
    private TestContext _ctx = null!;
    private InMemFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsReview_{Guid.NewGuid()}")
            .Options;
        _dbFactory = new InMemFactory(options);
        _ctx.Services.AddSingleton<ISyncJobRunner>(new Mock<ISyncJobRunner>().Object);
        _ctx.Services.AddSingleton<IMediaServerDirectory>(new Mock<IMediaServerDirectory>().Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private void SeedScanLocations(params string[] paths)
    {
        using var db = _dbFactory.CreateDbContext();
        foreach (var p in paths)
            db.ScanLocations.Add(new ScanLocationEntity { Path = p });
        db.SaveChanges();
    }

    private void SeedPlaylist(string id, string name, Platform platform)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = id,
            Name = name,
            SourcePlatform = platform,
            SourceUrl = "stub:" + id,
        });
        db.SaveChanges();
    }

    private IRenderedComponent<SyncJobs> WalkToReviewWithPlaylistSource()
    {
        SeedPlaylist("p1", "My Summer Hits", Platform.Spotify);
        SeedScanLocations("/music/a");
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        // selects: [0] = source type, [1] = source playlist
        cut.FindAll("select")[1].Change("p1");
        for (var guard = 0; guard < 4; guard++)
        {
            if (cut.FindAll("input[placeholder='/app/playlists/playlist.m3u']").Count > 0)
                break;
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        }
        cut.Find("input[placeholder='/app/playlists/playlist.m3u']")
            .Change("/app/playlists/out.m3u");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click(); // remaps
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click(); // review
        return cut;
    }

    [Test]
    public void Review_PlaylistSource_ShowsNameAndPlatform_NotRawGuid()
    {
        var cut = WalkToReviewWithPlaylistSource();
        Assert.That(cut.Markup, Does.Contain("My Summer Hits (Spotify)"),
            "the review Source line must show name + platform");
        Assert.That(cut.Markup, Does.Not.Contain(">p1<"),
            "the raw source id must not be rendered");
        Assert.That(cut.Markup, Does.Not.Contain("1aa7c983"),
            "a raw GUID must never appear");
    }

    [Test]
    public void Review_EveryFolderSummary_WhenNothingSelected()
    {
        var cut = WalkToReviewWithPlaylistSource();
        Assert.That(cut.Markup, Does.Contain("Every folder"),
            "empty selection reviews as \"Every folder\"");
    }

    [Test]
    public void Review_FolderSource_ShowsFolderPath()
    {
        SeedScanLocations("/music/folder");
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        cut.Find("select").Change("folder");
        cut.FindAll("select").Last().Change("/music/folder");
        for (var guard = 0; guard < 4; guard++)
        {
            if (cut.FindAll("input[placeholder='/app/playlists/playlist.m3u']").Count > 0)
                break;
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        }
        cut.Find("input[placeholder='/app/playlists/playlist.m3u']")
            .Change("/app/playlists/out.m3u");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();

        Assert.That(cut.Markup, Does.Contain("/music/folder"));
        Assert.That(cut.Markup, Does.Contain("All known"),
            "folder jobs pre-check the source folder, so the summary is All known");
    }

    [Test]
    public void EditingHeader_ShowsStepTitle_AndEditingSubtitle()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-e",
                Name = "Weekly Sync",
                SourceId = "p-none",
                SearchLocationPaths = "[]",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out/x.m3u",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        Assert.That(cut.Markup, Does.Contain("Pick your playlist"),
            "the h2 is the step title, also while editing");
        Assert.That(cut.Markup, Does.Contain("Editing \"Weekly Sync\""),
            "a muted subtitle names the edited job");
        Assert.That(cut.Markup, Does.Not.Contain(">Edit sync job<"),
            "the old constant edit title is gone");
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
