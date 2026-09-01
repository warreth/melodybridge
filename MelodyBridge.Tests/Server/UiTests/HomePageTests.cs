using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The home page has two faces. A fresh install gets the intro with a live
/// checklist and a skip button; once setup is complete (or skipped) the
/// page becomes the dashboard with stats, connections, errors, sync runs
/// and playlist cards. These tests drive both with a real database.
/// </summary>
[TestFixture]
[Category("UI")]
public class HomePageTests
{
    private TestContext _ctx = null!;
    private Mock<IDownloaderRegistry> _providerRegistry = null!;
    private LogCollector _collector = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        // The dashboard's guided tour calls melody.spotlight; loose mode
        // lets JS interop run without scripted handlers.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _providerRegistry = new Mock<IDownloaderRegistry>();
        _collector = new LogCollector(maxEntries: 500);

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
        _ctx.Services.AddSingleton(new SettingsStore(dbFactory));
        _ctx.Services.AddSingleton<ILogCollector>(_collector);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            dbFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new MelodyBridge.Infrastructure.MediaServers.ConfigJellyfinSettings(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    // ── Intro ──────────────────────────────────────────────────────

    [Test]
    public void FreshInstall_ShowsIntroWithChecklistAndSkip()
    {
        // Fresh install: no playlists, downloads or jobs anywhere.
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        Assert.That(cut.Markup, Does.Contain("Get everything running"),
            "the intro headline must appear for a fresh install");
        Assert.That(cut.Markup, Does.Contain("Add a playlist"));
        Assert.That(cut.Markup, Does.Contain("Download the music"));
        Assert.That(cut.Markup, Does.Contain("Publish it"));
        Assert.That(cut.Markup, Does.Contain("Skip intro"),
            "the intro must be skippable, it is optional");
    }

    [Test]
    public void Intro_Skipped_WhenDatabaseAlreadyHasData()
    {
        // Setup seeds a playlist, a downloaded track and a job: the intro
        // would render fully done, so it must never appear at all.
        var cut = _ctx.Render<Home>();

        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("Get everything running"),
            "a completed setup goes straight to the dashboard");
    }

    [Test]
    public async Task Intro_SelfDestruct_PersistsDismissal()
    {
        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));

        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AnyAsync(s => s.Key == "intro_dismissed"),
            Is.True, "the dismissal must persist so the intro never returns");
    }

    [Test]
    public async Task SkipButton_HidesIntro_AndPersists()
    {
        // Empty database: steps are not done, the intro stays until skipped.
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Skip intro"), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Skip intro")).Click();

        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("Get everything running"),
            "skipping must replace the intro with the dashboard immediately");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.That(await db.DownloaderSettings.AnyAsync(s => s.Key == "intro_dismissed"),
                Is.True, "the skip must be remembered across restarts");
        }
    }

    [Test]
    public async Task DismissedFlag_ShowsDashboard_Directly()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("Get everything running"),
            "a dismissed intro must never come back");
    }

    // ── Dashboard ──────────────────────────────────────────────────

    [Test]
    public async Task Dashboard_ShowsStatsConnectionsRunsAndPlaylistCards()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SyncJobRuns.Add(new SyncJobRunEntity
            {
                SyncJobId = "j1", Timestamp = DateTime.UtcNow, Status = "Completed",
                Message = "Synced 5/5 tracks",
            });
            db.SaveChanges();
        }
        _collector.Log(LogLevel.Error, "MelodyBridge.Application.Services.DownloadManager", "plugin exploded");

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("Connections"), "the connection panel must exist");
        Assert.That(cut.Markup, Does.Contain("Spotify"), "every provider must be listed");
        Assert.That(cut.Markup, Does.Contain("Jellyfin"));
        Assert.That(cut.Markup, Does.Contain("FlareSolverr"));

        Assert.That(cut.Markup, Does.Contain("plugin exploded"),
            "recent errors must surface on the dashboard");
        Assert.That(cut.Markup, Does.Contain("Test Job"),
            "the latest sync run must show the job name");
        Assert.That(cut.Markup, Does.Contain("Synced 5/5 tracks"));

        Assert.That(cut.Markup, Does.Contain("Techno"),
            "the playlist card must render");
        Assert.That(cut.Markup, Does.Contain("playlist-card"),
            "cards reuse the playlist styling");
    }

    [Test]
    public async Task Dashboard_EmptyDatabase_ShowsEmptyStates()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("No playlists yet"),
            "the playlist panel must explain what to do first");
        Assert.That(cut.Markup, Does.Contain("No errors"),
            "a clean log stream must say so instead of an empty hole");
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
