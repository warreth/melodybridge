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
/// Module B library UI, on a real SQLite database: the schedule picker
/// persists a chosen schedule, live monitoring is stored on the location,
/// each location card shows its MELODY_ID file count and last-scan time,
/// and the Docker mount-path warning is present on the add-location modal.
/// </summary>
[TestFixture]
[Category("UI")]
public class LibraryLocationsTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private Mock<ILibraryScanner> _scanner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-lib-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        _scanner = new Mock<ILibraryScanner>();
        _scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = "/music/rock",
                ScheduleCron = "0 9 * * 1",
                LiveMonitoring = true,
                LastScannedAt = new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc),
            });
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/jazz" }); // manual, never scanned
            db.Tracks.AddRange(
                new TrackEntity { MelodyId = "a", CurrentPath = "/music/rock/song.flac" },
                new TrackEntity { MelodyId = "b", CurrentPath = "/music/rock/deep/other.flac" },
                new TrackEntity { MelodyId = "c", CurrentPath = "/music/jazz/tune.mp3" },
                new TrackEntity { MelodyId = "d", CurrentPath = "/elsewhere/stray.flac" }, // outside both
                new TrackEntity { CurrentPath = "/music/rock/notagged.flac" }); // no MELODY_ID: not counted
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
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Test]
    public void LocationCard_ShowsFileCountAndLastScan()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Markup, Does.Contain("2 files with MELODY_ID"),
            "the rock folder holds exactly two tagged files (nested ones included)");
        Assert.That(cut.Markup, Does.Contain("1 files with MELODY_ID"),
            "the jazz folder holds exactly one tagged file");
        Assert.That(cut.Markup, Does.Contain("last scan 2026-07-06"),
            "the stored last-scan time renders on the card in a stable format");
        Assert.That(cut.Markup, Does.Contain("never"),
            "a location that was never scanned says so");
    }

    [Test]
    public void LocationCard_ShowsScheduleAndLiveBadge()
    {
        var cut = _ctx.Render<Library>();

        Assert.That(cut.Markup, Does.Contain("cron 0 9 * * 1"),
            "the stored cron schedule is described on the card");
        Assert.That(cut.Markup, Does.Contain("manual"),
            "a location without a schedule shows manual");
        Assert.That(cut.Markup, Does.Contain("live"),
            "the live monitoring state is visible per location");
    }

    [Test]
    public void AddLocationModal_ShowsDockerMountWarning()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location").Click();

        Assert.That(cut.Markup, Does.Contain("as the server sees it"),
            "the mount-path notice must be explicit, per the docs");
        Assert.That(cut.Markup, Does.Contain("container path"));
        Assert.That(cut.Markup, Does.Contain("compose.yml"));
    }

    [Test]
    public void AddLocationModal_HasScheduleModePicker()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location").Click();

        Assert.That(cut.Markup, Does.Contain("Manual only"));
        Assert.That(cut.Markup, Does.Contain("Every N minutes"));
        Assert.That(cut.Markup, Does.Contain("Cron / custom"));
    }

    [Test]
    public void SaveLocation_PersistsScheduleAndLiveMonitoring()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location").Click();

        // Fill the path.
        cut.Find("input[placeholder='/music']").Change("/music/new");

        // Pick "Every N minutes" in the schedule select (the first select on
        // the modal belongs to SchedulePicker's mode).
        var selects = cut.FindAll("select");
        selects[0].Change("Interval");
        cut.Render(); // let the picker push the new schedule up

        // Set 30 minutes.
        var numberInputs = cut.FindAll("input[type='number']");
        Assert.That(numberInputs.Count, Is.GreaterThan(0), "interval mode shows a minutes input");
        numberInputs[0].Change(30);
        cut.Render();

        // Live monitoring is on by default; submit.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location" && b.ClassList.Contains("primary")).Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var saved = db.ScanLocations.Single(l => l.Path == "/music/new");
        Assert.That(saved.ScheduleCron, Is.EqualTo("interval:30"));
        Assert.That(saved.LiveMonitoring, Is.True);
    }

    [Test]
    public void SaveLocation_WeekdayHourPickers_ComposeCron()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location").Click();
        cut.Find("input[placeholder='/music']").Change("/music/cronpick");

        var selects = cut.FindAll("select");
        selects[0].Change("Cron"); // mode
        cut.Render();

        selects = cut.FindAll("select");
        // In cron mode: mode, weekday, hour selects in DOM order.
        selects[1].Change("5"); // Friday
        cut.Render();
        selects = cut.FindAll("select");
        selects[2].Change("22"); // 22:00
        cut.Render();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add location" && b.ClassList.Contains("primary")).Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var saved = db.ScanLocations.Single(l => l.Path == "/music/cronpick");
        Assert.That(saved.ScheduleCron, Is.EqualTo("0 22 * * 5"),
            "weekday + hour pickers compose the cron string that lands in the DB");
    }

    [Test]
    public void RunScan_StampsLastScannedAt()
    {
        var cut = _ctx.Render<Library>();
        var before = cut.Markup;

        Assert.That(before, Does.Contain("never"), "jazz location starts never-scanned");

        cut.FindAll("button").First(b => b.TextContent.Contains("Run scan")).Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var all = db.ScanLocations.ToList();
        Assert.That(all, Has.All.Property("LastScannedAt").Not.Null,
            "every location gets a last-scan stamp after Run scan");
    }
}
