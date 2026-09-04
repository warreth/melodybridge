using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Editing an existing scan location: the location cards grow an Edit
/// button that reopens the same wizard modal, prefilled with the row's
/// path, schedule and live-monitoring state. Saving persists through real
/// SQLite; an empty path or a case-insensitive duplicate of another
/// location is rejected inline and leaves the database untouched.
/// </summary>
[TestFixture]
[Category("UI")]
public class LibraryLocationEditTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private Mock<ILibraryScanner> _scanner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-libedit-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        _scanner = new Mock<ILibraryScanner>();
        _scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(MelodyBridge.Core.ScanReport.Empty));

        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = "/music/rock",
                ScheduleCron = "0 9 * * 1", // Mondays 09:00
                LiveMonitoring = true,
            });
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/jazz", LiveMonitoring = false });
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

    /// <summary>Opens the edit modal for the location card with the given path.</summary>
    private IRenderedComponent<Library> OpenEdit(IRenderedComponent<Library> cut, string locationPath)
    {
        var card = cut.FindAll(".location-card").Single(c => c.TextContent.Contains(locationPath));
        card.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Edit").Click();
        cut.Render();
        return cut;
    }

    [Test]
    public void EditButton_OpensModal_Prefilled()
    {
        var cut = _ctx.Render<Library>();

        cut = OpenEdit(cut, "/music/rock");

        Assert.That(cut.Markup, Does.Contain("Edit scan location"),
            "the modal title switches to the edit variant");
        Assert.That(cut.Markup, Does.Contain("Editing: /music/rock"),
            "the eyebrow names the location being edited");

        var pathInput = cut.Find("input[placeholder='/music']");
        Assert.That(pathInput.GetAttribute("value"), Is.EqualTo("/music/rock"),
            "the directory path input is prefilled with the stored path");

        // SchedulePicker is seeded from the stored cron: mode select shows Cron.
        var modeSelect = cut.FindAll("select")[0];
        Assert.That(modeSelect.GetAttribute("value"), Is.EqualTo("Cron"),
            "the schedule picker reflects the seeded cron schedule");
        Assert.That(modeSelect.InnerHtml, Does.Contain("selected"),
            "the Cron option is marked selected");

        var toggle = cut.Find("label.toggle-switch.wide input[type='checkbox']");
        Assert.That(toggle.GetAttribute("checked"), Is.Not.Null,
            "live monitoring is on for the seeded location");
    }

    [Test]
    public void SaveEdit_UpdatesEntity_InDb()
    {
        var cut = _ctx.Render<Library>();
        cut = OpenEdit(cut, "/music/rock");

        // A real directory: saving refuses paths the app cannot see.
        var dir = Path.Combine(Path.GetTempPath(), $"mb-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        cut.Find("input[placeholder='/music']").Change(dir);
        cut.Find("label.toggle-switch.wide input[type='checkbox']").Change(false);
        cut.Render();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var saved = db.ScanLocations.Single(l => l.Path == dir);
        Assert.That(saved.LiveMonitoring, Is.False,
            "the live monitoring toggle flips off and persists");
        Assert.That(db.ScanLocations.Count(), Is.EqualTo(2),
            "editing never adds a row");
        Assert.That(cut.Markup, Does.Contain(dir),
            "the page re-renders the new path on the location card");
        Assert.That(cut.Markup, Does.Not.Contain("wizard-modal"),
            "the modal closes after a successful save");
    }

    [Test]
    public void SaveEdit_DuplicatePath_ShowsError_AndDoesNotSave()
    {
        var cut = _ctx.Render<Library>();
        cut = OpenEdit(cut, "/music/rock");

        // Same path as the jazz location, different case: still a duplicate.
        cut.Find("input[placeholder='/music']").Change("/MUSIC/JAZZ");
        cut.Render();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("already configured"),
            "the duplicate is reported inline in the modal");
        Assert.That(cut.Markup, Does.Contain("wizard-modal"),
            "the modal stays open so the user can fix the path");

        using var db = _factory.CreateDbContext();
        Assert.That(db.ScanLocations.AsNoTracking().Single(l => l.Id == 1).Path,
            Is.EqualTo("/music/rock"),
            "the edited row keeps its original path");
        Assert.That(db.ScanLocations.AsNoTracking().Single(l => l.Id == 2).Path,
            Is.EqualTo("/music/jazz"));
    }

    [Test]
    public void SaveEdit_EmptyPath_ShowsError()
    {
        var cut = _ctx.Render<Library>();
        cut = OpenEdit(cut, "/music/rock");

        cut.Find("input[placeholder='/music']").Change("");
        cut.Render();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").Click();
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("Enter a directory path."),
            "the empty path is rejected with an inline hint");
        Assert.That(cut.Markup, Does.Contain("wizard-modal"),
            "the modal stays open");

        using var db = _factory.CreateDbContext();
        Assert.That(db.ScanLocations.AsNoTracking().Single(l => l.Id == 1).Path,
            Is.EqualTo("/music/rock"), "nothing is saved");
    }

    [Test]
    public void Cancel_DoesNotSave()
    {
        var cut = _ctx.Render<Library>();
        cut = OpenEdit(cut, "/music/rock");

        cut.Find("input[placeholder='/music']").Change("/music/cancelled");
        cut.Find("label.toggle-switch.wide input[type='checkbox']").Change(false);
        cut.Render();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var row = db.ScanLocations.AsNoTracking().Single(l => l.Id == 1);
        Assert.That(row.Path, Is.EqualTo("/music/rock"), "cancel leaves the path alone");
        Assert.That(row.LiveMonitoring, Is.True, "cancel leaves the toggle alone");
        Assert.That(cut.Markup, Does.Contain("/music/rock"),
            "the card still shows the untouched path");
    }
}