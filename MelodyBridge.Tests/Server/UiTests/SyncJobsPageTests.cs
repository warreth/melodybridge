using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class SyncJobsPageTests
{
    private TestContext _ctx = null!;
    private Mock<ISyncJobRunner> _jobRunner = null!;
    private InMemFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _jobRunner = new Mock<ISyncJobRunner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);
        _dbFactory = dbFactory;

        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "src-1",
                Name = "Weekly Source",
                SourceUrl = "stub:src-1",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1",
                Name = "Weekly Sync",
                SourceId = "src-1",
                SearchLocationPaths = "[]",
                Schedule = "Daily",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/app/playlists/weekly.m3u",
                LastRunAt = DateTime.UtcNow.AddDays(-1),
                LastRunStatus = "Completed",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j2",
                Name = "Failed Job",
                Schedule = "Manual",
                OutputTarget = "Jellyfin",
                LastRunStatus = "Failed",
            });
            db.SaveChanges();
        }

        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void SyncJobs_Renders_Title()
    {
        var cut = _ctx.Render<SyncJobs>();
        Assert.That(cut.Markup, Does.Contain("Sync jobs"));
    }

    [Test]
    public void SyncJobs_ShowsJobList()
    {
        var cut = _ctx.Render<SyncJobs>();
        Assert.That(cut.Markup, Does.Contain("Weekly Sync"));
        Assert.That(cut.Markup, Does.Contain("Failed Job"));
    }

    [Test]
    public void SyncJobs_HasCreateNewButton()
    {
        var cut = _ctx.Render<SyncJobs>();
        var btns = cut.FindAll("button");
        Assert.That(btns.Any(b => b.TextContent.Trim().Contains("New sync job")), Is.True);
    }

    [Test]
    public void SyncJobs_ShowsRunButton()
    {
        var cut = _ctx.Render<SyncJobs>();
        var btns = cut.FindAll("button");
        Assert.That(btns.Any(b => b.TextContent.Trim().Contains("Run")), Is.True);
    }

    [Test]
    public void SyncJobs_OpensWizard_OnNewJob()
    {
        var cut = _ctx.Render<SyncJobs>();
        var btn = cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job"));
        btn.Click();
        Assert.That(cut.Markup, Does.Contain("New sync job"));
    }

    [Test]
    public void RunJobNow_CallsJobRunner()
    {
        _jobRunner.Setup(j => j.RunJobAsync(It.IsAny<SyncJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Completed, "ok", 1, 1));

        var cut = _ctx.Render<SyncJobs>();
        var btns = cut.FindAll("button");
        var runBtns = btns.Where(b => b.TextContent.Trim().Contains("Run")).ToList();
        if (runBtns.Count > 0)
        {
            runBtns[0].Click();
            _jobRunner.Verify(j => j.RunJobAsync(It.IsAny<SyncJob>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
    }

    [Test]
    public void EditButton_OpensWizard_HydratedWithJobValues()
    {
        var cut = _ctx.Render<SyncJobs>();
        var editBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit");
        editBtn.Click();

        Assert.That(cut.Markup, Does.Contain("Edit sync job"));
        var nameInput = cut.Find("input[placeholder='e.g. My Summer Hits']");
        Assert.That(nameInput.GetAttribute("value"), Is.EqualTo("Weekly Sync"));
    }

    [Test]
    public void EditJob_SaveUpdates_ExistingEntity_InDb()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        // walk to the review step and save (step 1 needs a source, and
        // the M3U path must stay filled to pass the new validation)
        for (int i = 0; i < 4; i++)
        {
            var next = cut.FindAll("button")
                .FirstOrDefault(b => b.TextContent.Trim() == "Next");
            if (next != null) next.Click();
        }

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        using var db = _dbFactory.CreateDbContext();
        var all = db.SyncJobs.IgnoreQueryFilters().ToList();
        Assert.That(all.Count, Is.EqualTo(2), "edit must update, not duplicate");
        var updated = all.First(j => j.Id == "j1");
        Assert.That(updated.Name, Is.EqualTo("Weekly Sync"));
        Assert.That(updated.M3uOutputPath, Is.EqualTo("/app/playlists/weekly.m3u"));
        Assert.That(updated.LastRunStatus, Is.EqualTo("Completed"), "edit must not reset run status");
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

}
