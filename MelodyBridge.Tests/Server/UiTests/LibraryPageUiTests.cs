using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The simplified Library page: it explains folder scanning and matching,
/// lists the scan folders and keeps the add-location wizard: but holds no
/// tracks table anymore, because track lists live on the playlist pages.
/// Real SQLite, the real scanner mocked only at the ILibraryScanner edge.
/// </summary>
[TestFixture]
[Category("UI")]
public class LibraryPageUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private Mock<ILibraryScanner> _scanner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-libui-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        _scanner = new Mock<ILibraryScanner>();
        _scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(MelodyBridge.Core.ScanReport.Empty));

        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/rock" });
            db.SaveChanges();
        }

        _ctx.Services.AddSingleton<ILibraryScanner>(_scanner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
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
    public void LibraryPage_ShowsTitleAndIntro()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Find("h1").TextContent, Is.EqualTo("Library"),
            "the page keeps its title");
        Assert.That(cut.Find(".page-title p").TextContent, Does.Contain("Folders that hold your music"),
            "the intro explains what the page does now");
        Assert.That(cut.Markup, Does.Contain("MELODY_ID"),
            "the page mentions the ID tag that drives matching");
    }

    [Test]
    public void LibraryPage_HasRunScanAndAddLocationButtons()
    {
        var cut = _ctx.Render<Library>();

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(buttons, Does.Contain("Run scan"));
        Assert.That(buttons, Does.Contain("Add music folder"));
    }

    [Test]
    public void LibraryPage_ShowsFoldersPanelWithLocation()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Markup, Does.Contain("Music folders"),
            "the folders panel names its section");
        Assert.That(cut.FindAll(".location-card").Count, Is.GreaterThan(0),
            "the configured location is listed");
        Assert.That(cut.Markup, Does.Contain("/music/rock"),
            "the location itself is listed");
    }

    [Test]
    public void LibraryPage_ListsFoldersAndDestinations_ButNoTracks()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.FindAll("section.lib-section"), Has.Count.EqualTo(2),
            "one section for the folders, one for the archive destinations");
        Assert.That(cut.Find(".lib-section").TextContent, Does.Contain("/music/rock"),
            "the configured folder is a row in the folders section");
        Assert.That(cut.FindAll("input.search-input"), Is.Empty,
            "track searching stays on the playlist pages");
        Assert.That(cut.FindAll("tr.track-row"), Is.Empty,
            "no track rows are rendered here");
    }

    [Test]
    public void LibraryPage_ExplainsMatchingInTheIntro_NotInAPanel()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Find(".page-title p").TextContent, Does.Contain("MELODY_ID"),
            "the intro names the tag that drives matching");
        Assert.That(cut.FindAll(".help-tip"), Is.Not.Empty,
            "the long explanation stays one click away");
        Assert.That(cut.Markup, Does.Not.Contain("How matching works"),
            "the page keeps no second copy of the docs");
    }
}
