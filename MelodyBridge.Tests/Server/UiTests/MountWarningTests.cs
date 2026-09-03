using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The shared Docker mount warning: the exact same notice on every
/// screen that takes a filesystem path, so a Docker user cannot miss
/// the host-path-versus-container-path rule. Rendered pages on a real
/// database; the warning markup and wording are asserted per screen.
/// </summary>
[TestFixture]
[Category("UI")]
public class MountWarningTests
{
    private Bunit.TestContext _ctx = null!;
    private TestSqliteFactory _factory = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-mount-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            // A scan location so the SyncJobs folder step renders its list.
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music" });
            db.SaveChanges();
        }

        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            _factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        _ctx.Services.AddSingleton(new Moq.Mock<MelodyBridge.Core.IMediaServerDirectory>().Object);
        _ctx.Services.AddSingleton<MelodyBridge.Core.ISyncJobRunner>(new Moq.Mock<MelodyBridge.Core.ISyncJobRunner>().Object);
        _ctx.Services.AddSingleton<MelodyBridge.Core.ILibraryScanner>(new Moq.Mock<MelodyBridge.Core.ILibraryScanner>().Object);
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    /// <summary>The one wording every screen shares; a change here is
    /// visible on all four screens at once.</summary>
    private void AssertSharedWarning(string markup)
    {
        Assert.That(markup, Does.Contain("mount-warning"),
            "the shared warning component renders");
        Assert.That(markup, Does.Contain("Docker users: type the path as the container sees it."),
            "the headline names the rule");
        Assert.That(markup, Does.Contain("zero files"),
            "the consequence is spelled out");
    }

    [Test]
    public void Settings_PathsTab_ShowsTheSharedWarning()
    {
        var cut = _ctx.Render<Settings>();
        // Default tab is Accounts; switch to paths.
        cut.FindAll("button, a").First(e => e.TextContent.Trim() == "Default paths").Click();
        cut.Render();

        AssertSharedWarning(cut.Markup);
    }

    [Test]
    public void SyncJobs_FolderStep_ShowsTheSharedWarning()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("New sync job")), TimeSpan.FromSeconds(3));
        // Open the wizard: first step is the source picker.
        cut.FindAll("button").First(b => b.TextContent.Contains("New sync job")).Click();
        cut.Render();

        // Folder source: the seeded /music scan location is the source,
        // which satisfies the step-0 validation. Re-find elements after
        // every render: bUnit invalidates nodes on re-render.
        cut.FindAll("select")[0].Change("folder");       // source type
        cut.Render();
        cut.FindAll("select")[1].Change("/music");       // the seeded folder
        cut.Render();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        cut.Render();

        AssertSharedWarning(cut.Markup);
    }

    [Test]
    public void Library_AddLocationModal_ShowsTheSharedWarning()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location").Click();
        cut.Render();

        AssertSharedWarning(cut.Markup);
    }

    [Test]
    public void Home_SetupChecklist_ShowsTheSharedWarning()
    {
        // Fresh database: setup mode active (zero done steps).
        var cut = _ctx.Render<Home>();

        AssertSharedWarning(cut.Markup);
    }
}
