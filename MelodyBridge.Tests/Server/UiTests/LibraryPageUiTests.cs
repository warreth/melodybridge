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
            .Returns(Task.CompletedTask);

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

        Assert.That(cut.Markup, Does.Contain("<h1>Library</h1>"),
            "the page keeps its title");
        Assert.That(cut.Markup, Does.Contain("Scan folders with music you already own"),
            "the intro explains what the page does now");
        Assert.That(cut.Markup, Does.Contain("matches them to your playlist tracks"),
            "the intro mentions the ID-tag matching");
    }

    [Test]
    public void LibraryPage_HasRunScanAndAddLocationButtons()
    {
        var cut = _ctx.Render<Library>();

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(buttons, Does.Contain("Run scan"));
        Assert.That(buttons, Does.Contain("Add location"));
    }

    [Test]
    public void LibraryPage_ShowsFoldersPanelWithLocation()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Markup, Does.Contain("Music folders (1)"),
            "the folders panel counts the configured location");
        Assert.That(cut.Markup, Does.Contain("/music/rock"),
            "the location itself is listed");
    }

    [Test]
    public void LibraryPage_HasNoTracksTable()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Markup, Does.Not.Contain("<table"),
            "the library page no longer renders any table");
        Assert.That(cut.Markup, Does.Not.Contain("modern-table"),
            "and the table styling class is gone with it");
        Assert.That(cut.FindAll("input.search-input"), Is.Empty,
            "the tracks search box is gone with the table");
    }

    [Test]
    public void LibraryPage_MatchingPanelIsHonest()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Markup, Does.Contain("How matching works"));
        Assert.That(cut.Markup, Does.Contain("Matched"),
            "the old file-count line is replaced by what matching does");
        Assert.That(cut.Markup, Does.Not.Contain("files in library"),
            "no stale database file count");
    }
}
