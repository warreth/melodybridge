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
/// Dashboard UI with a real in-memory database: the three-step workflow
/// reflects live database state, and step links point to the right pages.
/// </summary>
[TestFixture]
[Category("UI")]
public class HomePageTests
{
    private TestContext _ctx = null!;
    private Mock<IDownloaderRegistry> _providerRegistry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _providerRegistry = new Mock<IDownloaderRegistry>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"HomeTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity { Id = "p1", Name = "Techno", SourceUrl = "s:1", TrackCount = 101 });
            db.Tracks.Add(new TrackEntity { Id = 1, Title = "Downloaded", DownloadStatus = "downloaded", CurrentPath = "/x/1.mp3" });
            db.SyncJobs.Add(new SyncJobEntity { Id = "j1", Name = "Test Job", LastRunStatus = "Completed" });
            db.SaveChanges();
        }

        _providerRegistry.Setup(r => r.GetAll()).Returns(new List<IDownloader>());
        _providerRegistry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);

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
    public void Dashboard_ShowsThreeStepWorkflow()
    {
        var cut = _ctx.Render<Home>();

        Assert.That(cut.Markup, Does.Contain("Three steps, in order"));
        Assert.That(cut.Markup, Does.Contain("Add a playlist"));
        Assert.That(cut.Markup, Does.Contain("Download the music"));
        Assert.That(cut.Markup, Does.Contain("Publish it"));
    }

    [Test]
    public void Dashboard_StepsLinkToTheirPages()
    {
        var cut = _ctx.Render<Home>();

        var steps = cut.FindAll(".workflow-step");
        Assert.That(steps, Has.Count.EqualTo(3));
        Assert.That(steps[0].GetAttribute("href"), Is.EqualTo("playlists"));
        Assert.That(steps[1].GetAttribute("href"), Is.EqualTo("downloads"));
        Assert.That(steps[2].GetAttribute("href"), Is.EqualTo("sync-jobs"));
    }

    [Test]
    public void Dashboard_StepsDone_WhenDatabaseHasData()
    {
        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("101 playlist(s) saved")
            || cut.FindAll(".workflow-step.done").Count == 3, TimeSpan.FromSeconds(3));

        var done = cut.FindAll(".workflow-step.done");
        Assert.That(done, Has.Count.EqualTo(3),
            "with playlists, downloads and jobs in the DB, all steps must show as done");
    }

    [Test]
    public void Dashboard_StepsTodo_WhenDatabaseIsEmpty()
    {
        // Wipe everything the Setup inserted.
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using var db = dbFactory.CreateDbContext();
        db.Playlists.RemoveRange(db.Playlists);
        db.Tracks.RemoveRange(db.Tracks);
        db.SyncJobs.RemoveRange(db.SyncJobs);
        db.SaveChanges();

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.FindAll(".workflow-step.done").Count == 0, TimeSpan.FromSeconds(3));

        Assert.That(cut.FindAll(".workflow-step.done"), Is.Empty,
            "an empty database must show all steps as not done");
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
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
