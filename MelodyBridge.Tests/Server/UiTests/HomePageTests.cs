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
    private Mock<ISyncJobRunner> _jobRunner = null!;
    private Mock<IDownloaderRegistry> _providerRegistry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _jobRunner = new Mock<ISyncJobRunner>();
        _providerRegistry = new Mock<IDownloaderRegistry>();

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

        // Setup provider registry defaults
        _providerRegistry.Setup(r => r.GetAll()).Returns(new List<IDownloader>());
        _providerRegistry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);

        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IDownloaderRegistry>(_providerRegistry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);

        var downloadManager = new Application.Services.DownloadManager(
            new EmptyRegistry(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.DownloadManager>.Instance);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.PlaylistStore(
            dbFactory,
            Array.Empty<MelodyBridge.Core.ISourceProvider>(),
            downloadManager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Services.PlaylistStore>.Instance));
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
        Assert.That(texts, Has.Some.Contains("Sync due playlists"));
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
    public void SyncDuePlaylists_Click_RendersWithoutError()
    {
        var cut = _ctx.Render<Home>();
        var buttons = cut.FindAll("button");
        var syncBtn = buttons.First(b => b.TextContent.Trim().Contains("Sync due playlists"));
        syncBtn.Click();
        // No exception thrown through bUnit's unhandled-exception assertion.
        Assert.That(cut.Markup, Does.Contain("Sync due playlists"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
    }
}
