using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TestContext = Bunit.TestContext;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Interaction tests for the refactored pages: the card menu delete flow,
/// the logs toolbar toggle, plugin reorder buttons, the sync job footer,
/// settings status pills and the dashboard connection rows. Every test
/// clicks real elements and asserts real rendered state.
/// </summary>
[TestFixture]
[Category("UI")]
public class PageRefactorUiTests
{
    private TestContext _ctx = null!;
    private string _dbPath = null!;
    private IDbContextFactory<MelodyBridgeDbContext> _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-ref-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            _factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        // Settings injects IConfiguration; SyncJobs injects ISyncJobRunner.
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.Services.AddSingleton<MelodyBridge.Core.ISyncJobRunner>(new NoopJobRunner());
        var collector = new LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new LogExporter(collector));
        // Settings injects the media server directory; the default mock does nothing.
        _ctx.Services.AddSingleton(new Moq.Mock<MelodyBridge.Core.IMediaServerDirectory>().Object);
        // The dashboard's guided tour calls melody.spotlight; loose mode lets
        // JS interop run without scripted handlers.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Stand-in runner: the page renders jobs without executing them.</summary>
    private sealed class NoopJobRunner : MelodyBridge.Core.ISyncJobRunner
    {
        public Task<MelodyBridge.Core.SyncJobRunLog> RunJobAsync(
            MelodyBridge.Core.SyncJob job, CancellationToken ct = default)
            => Task.FromResult(new MelodyBridge.Core.SyncJobRunLog(
                DateTime.UtcNow, MelodyBridge.Core.SyncStatus.Completed, "noop", 0, 0));
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

    private void SeedOnePlaylist()
    {
        using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-menu",
            Name = "Menu Mix",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TrackCount = 2,
        });
        db.SaveChanges();
    }

    [Test]
    public void Playlists_DeleteLivesInCardMenu_NotAFloatingX()
    {
        SeedOnePlaylist();
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Menu Mix")), TimeSpan.FromSeconds(3));

        var card = cut.Find(".playlist-card");
        Assert.That(card.QuerySelector("details.card-menu"), Is.Not.Null,
            "the card carries the shared action menu");
        Assert.That(card.QuerySelector("summary.card-menu-trigger"), Is.Not.Null,
            "the trigger is the three-dot summary");
        Assert.That(cut.FindAll("button[title='Remove this playlist']").Count(), Is.EqualTo(1),
            "the remove action sits inside the menu");
    }

    [Test]
    public void Playlists_CardMenuRemove_OpensConfirmDialog()
    {
        SeedOnePlaylist();
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Menu Mix")), TimeSpan.FromSeconds(3));

        cut.Find("button[title='Remove this playlist']").Click();

        Assert.That(cut.Markup, Does.Contain("Remove 'Menu Mix'?"),
            "the confirm dialog opens from the menu item");
    }

    [Test]
    public void Logs_NewestFirstToggleChangesOrder()
    {
        var collector = new LogCollector(maxEntries: 500);
        _ctx.Services.AddSingleton<ILogCollector>(collector);
        _ctx.Services.AddSingleton(new LogExporter(collector));

        collector.Log(LogLevel.Info, "Scanner", "first message");
        collector.Log(LogLevel.Info, "Scanner", "second message");

        var cut = _ctx.Render<Logs>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("second message")), TimeSpan.FromSeconds(3));

        Assert.That(cut.FindAll(".logs-toolbar").Count(), Is.EqualTo(1),
            "the search and chips share one sticky toolbar");
        var toggle = cut.Find("label.toggle-switch.wide input[type='checkbox']");
        Assert.That(toggle.HasAttribute("checked"),
            "newest first is on by default so the tail stays visible");

        var rows = cut.FindAll(".logs-list .log-row");
        Assert.That(rows.First().TextContent, Does.Contain("second message"),
            "the newest entry renders at the top by default");

        toggle.Change(false);

        rows = cut.FindAll(".logs-list .log-row");
        Assert.That(rows.First().TextContent, Does.Contain("first message"),
            "turning the toggle off restores oldest-first order");
    }

    [Test]
    public void Settings_TabStripKeepsContractAndPillsRender()
    {
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Spotify &amp; YouTube")), TimeSpan.FromSeconds(3));

        var tabs = cut.FindAll("button.tab-link").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(tabs, Is.EqualTo(new[]
        {
            "Accounts", "Media servers", "Default paths", "Quality", "Network", "About",
        }), "the six tabs keep their labels and order");

        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "Network").Click();

        Assert.That(cut.Markup, Does.Contain("FlareSolverr (Cloudflare)"),
            "the network tab opens on click");
        Assert.That(cut.FindAll("span.pill.neutral").Count(), Is.GreaterThan(0),
            "the not-tested status renders as a neutral pill");
    }

    [Test]
    public void Plugins_MoveButtonsStayInTheButtonGroup()
    {
        var providers = new List<IDownloader>
        {
            new TestDownloader("first", "First Plugin"),
            new TestDownloader("second", "Second Plugin"),
        };
        var allProviders = providers;
        var registry = new Mock<IDownloaderRegistry>();
        registry.Setup(r => r.GetAll()).Returns(allProviders);
        registry.Setup(r => r.GetEnabled()).Returns(allProviders);
        registry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);
        registry.Setup(r => r.SetOrderAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ctx.Services.AddSingleton<IDownloaderRegistry>(registry.Object);

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("First Plugin")), TimeSpan.FromSeconds(3));

        var card = cut.Find(".plugin-card");
        Assert.That(card.QuerySelector(".btn-group"), Is.Not.Null,
            "the reorder arrows share one compact group");
        Assert.That(card.QuerySelector("label.toggle-switch"), Is.Not.Null,
            "the enable toggle stays on the card");

        var moveButtons = cut.FindAll("button[title='Try this plugin first']");
        Assert.That(moveButtons.Count(), Is.EqualTo(2),
            "every plugin except the first-disabled keeps its move-up button");
        moveButtons[1].Click();

        registry.Verify(r => r.SetOrderAsync(It.Is<IReadOnlyList<string>>(
            o => o[0] == "second" && o[1] == "first"), It.IsAny<CancellationToken>()), Times.Once,
            "clicking the arrow writes the new order to the registry");
    }

    [Test]
    public void SyncJobs_CardActionsSitInTheFooter()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-1",
                Name = "Footer Job",
                OutputTarget = "M3uFile",
                LastRunStatus = "Completed",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Footer Job")), TimeSpan.FromSeconds(3));

        var footer = cut.Find(".job-footer");
        var labels = footer.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(labels, Is.EqualTo(new[] { "Run now", "Log", "Edit", "Delete" }),
            "the four actions align in one inline footer");
        Assert.That(footer.QuerySelector("span.pill"), Is.Null,
            "the status pill stays in the header, not the footer");
    }

    [Test]
    public void Playlists_ImportPanel_UsesStatusPillAndSecondaryButtons()
    {
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Add playlist")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Import").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Exportify CSV")), TimeSpan.FromSeconds(3));

        Assert.That(cut.FindAll("span.pill.ok").Count(), Is.GreaterThan(0),
            "the recommended badge renders through the shared pill");
        Assert.That(cut.FindAll("span.pill.neutral").Any(p => p.TextContent.Trim() == "manual, no API"),
            Is.True, "the Spotify data export card keeps its neutral badge");
        Assert.That(cut.FindAll("span.pill.neutral").Any(p => p.TextContent.Trim() == "not connected"),
            Is.True, "the unlinked account card shows its neutral state");
        var upload = cut.Find("input[type='file']");
        Assert.That(upload.ClassList, Does.Contain("btn-modern"),
            "the file inputs keep the button look");
        Assert.That(cut.FindAll("button.btn-modern:not(.secondary):not(.primary):not(.danger):not(.ghost)").Count(),
            Is.EqualTo(0), "every wizard button carries an explicit variant");
    }

    [Test]
    public void Home_ConnectionsShowBrandIconsAndStatusPills()
    {
        var registry = new Mock<IDownloaderRegistry>();
        registry.Setup(r => r.GetAll()).Returns(new List<IDownloader>());
        registry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);
        _ctx.Services.AddSingleton<IDownloaderRegistry>(registry.Object);
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new MelodyBridge.Infrastructure.MediaServers.ConfigJellyfinSettings(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(
            new TestServices.FixedPlexSettings());
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(
            new TestServices.FixedNavidromeSettings());

        var cut = _ctx.Render<Home>();
        // Fresh install: dismiss the intro so the dashboard renders.
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Skip intro")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Skip intro")).Click();
        cut.WaitForState(() => cut.Markup.Contains("Connections"), TimeSpan.FromSeconds(3));

        var rows = cut.FindAll(".connection-row");
        Assert.That(rows.Count(), Is.EqualTo(6), "all six connections render");
        Assert.That(rows.First().QuerySelector("svg.brand-icon"), Is.Not.Null,
            "the Spotify row leads with its brand mark");
        Assert.That(rows.First().QuerySelector("span.pill"), Is.Not.Null,
            "the status renders as a pill on the same row");

        var flares = rows.Single(r => r.TextContent.Contains("FlareSolverr"));
        Assert.That(flares.QuerySelector("span.pill"), Is.Not.Null,
            "FlareSolverr keeps its status pill");
    }
}