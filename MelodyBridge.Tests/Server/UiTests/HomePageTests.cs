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
public class HomePageTests
{
    private TestContext _ctx = null!;
    private Mock<IMusicSourceManager> _sourceMgr = null!;
    private Mock<ISyncJobRunner> _jobRunner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _sourceMgr = new Mock<IMusicSourceManager>();
        _jobRunner = new Mock<ISyncJobRunner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"HomeTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        using (var db = dbFactory.CreateDbContext())
        {
            db.Sources.Add(new SourceEntity { Id = "s1", Name = "Test Source", Platform = "YouTubeMusic" });
            db.Tracks.Add(new TrackEntity { Id = 1, Title = "Test Track" });
            db.SyncJobs.Add(new SyncJobEntity { Id = "j1", Name = "Test Job", LastRunStatus = "Pending" });
            db.SyncJobs.Add(new SyncJobEntity { Id = "j2", Name = "Done Job", LastRunStatus = "Completed" });
            db.SaveChanges();
        }

        _ctx.Services.AddSingleton<IMusicSourceManager>(_sourceMgr.Object);
        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Dashboard_Renders_WithTitle()
    {
        var cut = _ctx.Render<Home>();
        Assert.That(cut.Markup, Does.Contain("Bridge your playlists, downloads and media server."));
    }

    [Test]
    public void Dashboard_HasQuickActionButtons()
    {
        var cut = _ctx.Render<Home>();
        var buttons = cut.FindAll("button");
        Assert.That(buttons.Count, Is.GreaterThanOrEqualTo(3));
        var texts = buttons.Select(b => b.TextContent.Trim()).ToList();
        Assert.That(texts, Has.Some.Contains("Run all sync jobs"));
        Assert.That(texts, Has.Some.Contains("Auto-sync sources"));
        Assert.That(texts, Has.Some.Contains("Refresh stats"));
    }

    [Test]
    public void Dashboard_ShowsHealthSection()
    {
        var cut = _ctx.Render<Home>();
        Assert.That(cut.Markup, Does.Contain("Server"));
        Assert.That(cut.Markup, Does.Contain("Database"));
        Assert.That(cut.Markup, Does.Contain("Health"));
    }

    [Test]
    public void Dashboard_DisplaysTrackAndSourceCounts()
    {
        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("1 tracks"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("1 tracks"));
    }

    [Test]
    public void RunAllSync_Click_CallsJobRunner()
    {
        _jobRunner.Setup(j => j.RunJobAsync(It.IsAny<SyncJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Completed, "ok", 1, 1));

        var cut = _ctx.Render<Home>();
        var btn = cut.Find("button.btn-modern.primary");
        btn.Click();

        _jobRunner.Verify(j => j.RunJobAsync(It.IsAny<SyncJob>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public void AutoSyncAll_Click_CallsSourceManager()
    {
        _sourceMgr.Setup(s => s.AutoSyncAllAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = _ctx.Render<Home>();
        var buttons = cut.FindAll("button");
        var autoSyncBtn = buttons.First(b => b.TextContent.Trim().Contains("Auto-sync sources"));
        autoSyncBtn.Click();

        _sourceMgr.Verify(s => s.AutoSyncAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}

