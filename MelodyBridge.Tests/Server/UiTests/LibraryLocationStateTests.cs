using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Folder addition and removal state tracking on the Library page, on a
/// real SQLite database: adding a location goes through the wizard modal
/// and lands as a row plus a rendered card; removal asks for
/// confirmation, keeps the row until confirmed, then deletes it and
/// drops the card. Cancel keeps everything. Nothing here is stubbed
/// except the scanner edge, so the assertions trace real state.
/// </summary>
[TestFixture]
[Category("UI")]
public class LibraryLocationStateTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private Mock<ILibraryScanner> _scanner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-libstate-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        _scanner = new Mock<ILibraryScanner>();
        _scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScanReport.Empty);

        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = "/music/rock",
                ScheduleCron = "0 9 * * 1",
                LiveMonitoring = true,
            });
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

    private void OpenAddModal(IRenderedComponent<Library> cut)
    {
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add location").Click();
        cut.Render();
    }

    [Test]
    public void AddLocation_TracksState_InDbAndOnCards()
    {
        // A real directory: the save now rejects paths the app cannot see.
        var metalDir = Path.Combine(Path.GetTempPath(), $"mb-metal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(metalDir);
        try
        {
            var cut = _ctx.Render<Library>();
            Assert.That(cut.FindAll(".location-card").Count, Is.EqualTo(1),
                "the seeded location renders one card");

            OpenAddModal(cut);
            cut.Find("input[placeholder='/music']").Change("/music/metal-never-created");

            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add location" && b.ClassList.Contains("primary")).Click();
            cut.WaitForAssertion(() => Assert.That(cut.Markup,
                Does.Contain("That folder does not exist"),
                "a path the app cannot see is rejected inline"));

            using (var db = _factory.CreateDbContext())
                Assert.That(db.ScanLocations.Count(), Is.EqualTo(1),
                    "the rejected add never writes a row");

            cut.Find("input[placeholder='/music']").Change(metalDir);
            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Add location" && b.ClassList.Contains("primary")).Click();

            // SaveLocation runs a full async pipeline through SQLite; wait for
            // the page to re-render with the new card before asserting state.
            cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Music folders (2)"),
                "the panel header counts both locations"));

            using (var db = _factory.CreateDbContext())
            {
                var rows = db.ScanLocations.AsNoTracking().ToList();
                Assert.That(rows.Count, Is.EqualTo(2), "the add lands as a second row");
                var added = rows.Single(l => l.Path == metalDir);
                Assert.That(added.ScheduleCron, Is.EqualTo(""),
                    "manual is the default schedule and serializes as an empty string");
                Assert.That(added.LiveMonitoring, Is.True, "live monitoring is the default for a new location");
            }

            Assert.That(cut.FindAll(".location-card").Count, Is.EqualTo(2),
                "the page tracks the new state without a manual reload");
            Assert.That(cut.Markup, Does.Contain(metalDir),
                "the new card shows the added path");
            Assert.That(cut.Markup, Does.Not.Contain("wizard-modal"),
                "the modal closes after the add");
        }
        finally
        {
            try { Directory.Delete(metalDir); } catch { /* best effort */ }
        }
    }

    [Test]
    public void RemoveLocation_AskedFor_StillTracksRow()
    {
        var cut = _ctx.Render<Library>();

        cut.FindAll(".location-card").Single(c => c.TextContent.Contains("/music/rock"))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("Remove '/music/rock'?"),
            "the confirmation names the folder it is about to remove");

        using (var db = _factory.CreateDbContext())
            Assert.That(db.ScanLocations.Count(), Is.EqualTo(1),
                "the row survives until the removal is confirmed");

        Assert.That(cut.FindAll(".location-card").Count, Is.EqualTo(1),
            "the card also survives the question");
    }

    [Test]
    public void RemoveLocation_Confirmed_TracksDeletedState()
    {
        var cut = _ctx.Render<Library>();

        cut.FindAll(".location-card").Single(c => c.TextContent.Contains("/music/rock"))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();
        cut.Render();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove folder").Click();
        cut.Render();

        using (var db = _factory.CreateDbContext())
            Assert.That(db.ScanLocations.Count(), Is.EqualTo(0),
                "the confirmed removal deletes the row");

        Assert.That(cut.FindAll(".location-card").Count, Is.EqualTo(0),
            "the page drops the card with the row");
        Assert.That(cut.Markup, Does.Contain("No scan locations"),
            "the empty state takes over when the last location goes");
        Assert.That(cut.Markup, Does.Contain("Music folders (0)"),
            "the panel header counts zero locations");
    }

    [Test]
    public void RemoveLocation_Cancelled_KeepsState()
    {
        var cut = _ctx.Render<Library>();

        cut.FindAll(".location-card").Single(c => c.TextContent.Contains("/music/rock"))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Remove").Click();
        cut.Render();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        cut.Render();

        using (var db = _factory.CreateDbContext())
            Assert.That(db.ScanLocations.Count(), Is.EqualTo(1),
                "cancel leaves the row in place");

        Assert.That(cut.FindAll(".location-card").Count, Is.EqualTo(1),
            "cancel leaves the card rendered");
        Assert.That(cut.Markup, Does.Not.Contain("Remove '/music/rock'?"),
            "the question is gone after cancelling");
    }
}
