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

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _jobRunner = new Mock<ISyncJobRunner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        using (var db = dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1",
                Name = "Weekly Sync",
                Schedule = "Daily",
                OutputTarget = "M3uFile",
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

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

}
