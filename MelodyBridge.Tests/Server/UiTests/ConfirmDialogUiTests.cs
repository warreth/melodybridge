using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Destructive actions across pages share one dialog and one rule: ask
/// first, then act. These drive the real pages against real SQLite.
/// </summary>
[TestFixture]
[Category("UI")]
public class ConfirmDialogUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-confirm-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
        // Library injects the scanner; a no-op stub is enough here.
        _ctx.Services.AddSingleton<ILibraryScanner>(new Moq.Mock<ILibraryScanner>().Object);
        // Sync jobs inject the runner; the delete flow must not run a job.
        _ctx.Services.AddSingleton<ISyncJobRunner>(new Moq.Mock<ISyncJobRunner>().Object);
        // Settings reads app config and the account providers.
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(_factory,
            NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new Moq.Mock<MelodyBridge.Core.IMediaServerDirectory>().Object);
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public void Library_RemoveAsksFirst_CancelKeepsTheFolder()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/keep" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("/music/keep")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();
        Assert.That(cut.Markup, Does.Contain("Remove '/music/keep'?"),
            "the dialog names the folder before anything happens");
        Assert.That(cut.Markup, Does.Contain("The music files inside it are not deleted"),
            "the dialog says the files stay");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        using (var db = _factory.CreateDbContext())
            Assert.That(db.ScanLocations.Count(), Is.EqualTo(1), "cancel keeps the row");
    }

    [Test]
    public void Library_RemoveConfirmed_DeletesTheFolderRow()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/gone" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("/music/gone")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove folder").Click();

        cut.WaitForAssertion(() =>
        {
            using var db = _factory.CreateDbContext();
            Assert.That(db.ScanLocations.Count(), Is.EqualTo(0), "confirm removes the row");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void SyncJobs_DeleteAsksFirst_AndDeletesWithHistory()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity { Id = "job-1", Name = "Weekly push", Schedule = "Weekly" });
            db.SyncJobRuns.Add(new SyncJobRunEntity { SyncJobId = "job-1", Status = "Completed" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Weekly push")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        Assert.That(cut.Markup, Does.Contain("Delete 'Weekly push'?"),
            "the dialog names the job");
        Assert.That(cut.Markup, Does.Contain("its run history"),
            "the dialog mentions what else disappears");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Delete job").Click();
        cut.WaitForAssertion(() =>
        {
            using var db = _factory.CreateDbContext();
            Assert.That(db.SyncJobs.Count(), Is.EqualTo(0), "the job row is gone");
            Assert.That(db.SyncJobRuns.Count(), Is.EqualTo(0), "the run history goes with it");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_ProfileDeleteAsksFirst()
    {
        var profiles = new MediaServerProfileStore(
            _ctx.Services.GetRequiredService<SettingsStore>());
        profiles.SaveAsync(new MediaServerProfile
        {
            Id = "srv-1", Name = "Attic server", Kind = "Jellyfin",
            BaseUrl = "http:// attic:8096".Replace(" ", ""), ApiKey = "k",
        }).Wait();

        var cut = _ctx.Render<Settings>();
        // Jump to the media servers tab.
        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "Media servers").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Attic server")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        Assert.That(cut.Markup, Does.Contain("Delete 'Attic server'?"),
            "the dialog names the profile before deletion");
        Assert.That(cut.Markup, Does.Contain("need a new server picked"),
            "the dialog warns about jobs depending on it");

        // Cancel keeps it.
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.That(profiles.GetAllAsync().Result.Count, Is.EqualTo(1),
            "cancel keeps the profile");
    }

    private sealed class TestSqliteFactory(string dbPath) : IDbContextFactory<MelodyBridgeDbContext>
    {
        public MelodyBridgeDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new MelodyBridgeDbContext(options);
        }

        public Task<MelodyBridgeDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
