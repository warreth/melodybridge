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
            .Returns(Task.FromResult(MelodyBridge.Core.ScanReport.Empty));

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

        // A table puts the count in its own column, so the assertion reads the
        // row for each folder instead of a sentence.
        var rock = cut.FindAll(".location-card").Single(r => r.TextContent.Contains("/music/rock"));
        var jazz = cut.FindAll(".location-card").Single(r => r.TextContent.Contains("/music/jazz"));
        Assert.That(rock.QuerySelector(".lib-row-main small")?.TextContent, Does.Contain("2 files"),
            "the rock folder holds exactly two tagged files (nested ones included)");
        Assert.That(jazz.QuerySelector(".lib-row-main small")?.TextContent, Does.Contain("1 file"),
            "the jazz folder holds exactly one tagged file");
        Assert.That(rock.TextContent, Does.Contain("2026-07-06"),
            "the stored last-scan time renders in a stable format");
        Assert.That(jazz.TextContent, Does.Contain("never"),
            "a location that was never scanned says so");
    }

    [Test]
    public void LocationCard_ShowsScheduleAndLiveBadge()
    {
        var cut = _ctx.Render<Library>();

        var rock = cut.FindAll(".location-card").Single(r => r.TextContent.Contains("/music/rock"));
        var jazz = cut.FindAll(".location-card").Single(r => r.TextContent.Contains("/music/jazz"));
        Assert.That(rock.TextContent, Does.Contain("cron 0 9 * * 1"),
            "the stored cron schedule is described on the folder's row");
        Assert.That(jazz.TextContent, Does.Contain("manual"),
            "a location without a schedule shows manual");
        Assert.That(rock.TextContent, Does.Contain("live"),
            "live monitoring is visible per location");
        Assert.That(jazz.TextContent, Does.Not.Contain("live"),
            "a folder without live monitoring does not claim it");
    }

    [Test]
    public async Task ArchiveProfileFolder_RegistersItselfAndShowsAsArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mb-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = _ctx.Services.GetRequiredService<ArchiveProfileStore>();
            await store.SaveAsync(new ArchiveProfileEntity
            {
                Id = "",
                Name = "Opus mirror",
                RootPath = root,
                Format = "opus:128",
                Enabled = true,
            });

            var cut = _ctx.Render<Library>();

            cut.WaitForAssertion(() =>
            {
                var archiveSection = cut.Find("[data-section='archive']");
                var card = archiveSection.QuerySelectorAll(".archive-card")
                    .Single(c => c.TextContent.Contains(root));
                Assert.Multiple(() =>
                {
                    Assert.That(card.TextContent, Does.Contain("Opus mirror"),
                        "a new profile shows its own row");
                    Assert.That(cut.Find("[data-section='folders']").TextContent,
                        Does.Not.Contain(root),
                        "the profile's folder stays out of the music folders");
                });
            }, TimeSpan.FromSeconds(3));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ArchiveProfiles_SaveAndDeleteOnTheLibraryPage()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mb-archive-{Guid.NewGuid():N}");
        var cut = _ctx.Render<Library>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Archive copies")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").First(b => b.TextContent.Contains("Add destination")).Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Add an archive destination")), TimeSpan.FromSeconds(3));
        var name = cut.FindAll(".wizard-modal label")
            .Single(l => l.TextContent.TrimStart().StartsWith("Name"))
            .QuerySelector("input")!;
        name.Change("Opus copy");
        var root = cut.FindAll(".wizard-modal label")
            .Single(l => l.TextContent.Contains("Folder"))
            .QuerySelector("input")!;
        root.Change(folder);
        cut.FindAll("button").First(b => b.TextContent.Contains("Save destination")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Opus copy"));
            Assert.That(cut.Markup, Does.Contain("Opus 128 kbps"));
        }, TimeSpan.FromSeconds(5));

        using (var db = _factory.CreateDbContext())
        {
            var saved = db.ArchiveProfiles.AsNoTracking().Single(p => p.Name == "Opus copy");
            Assert.Multiple(() =>
            {
                Assert.That(saved.RootPath, Is.EqualTo(folder.Trim()));
                Assert.That(saved.Format, Is.EqualTo("opus:128"));
                Assert.That(saved.Enabled, Is.True);
            });
        }

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button").Any(b => b.GetAttribute("aria-label") == "Delete Opus copy"),
                Is.True, "the saved destination offers a delete action"), TimeSpan.FromSeconds(5));
        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Delete Opus copy").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Delete Opus copy?")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete destination").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Not.Contain("Opus copy")), TimeSpan.FromSeconds(5));
        using (var db = _factory.CreateDbContext())
            Assert.That(db.ArchiveProfiles.Count(p => p.Name == "Opus copy"), Is.Zero);
    }

    [Test]
    public void Library_ShowsTheDefaultAndEachDestination()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.ArchiveProfiles.Add(new ArchiveProfileEntity
            {
                Id = "p-opus",
                Name = "Opus mirror",
                RootPath = "/archive-opus",
                Format = "opus:128",
                Enabled = true,
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Default archive"),
                "the default row sits above the named profiles");
            Assert.That(cut.Markup, Does.Contain("Opus mirror"));
            Assert.That(cut.Markup, Does.Contain("/archive-opus"));
            Assert.That(cut.Markup, Does.Contain("Opus 128 kbps"));
            Assert.That(cut.FindAll(".archive-card"), Has.Count.EqualTo(1));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Library_DestinationTogglePersists()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.ArchiveProfiles.Add(new ArchiveProfileEntity
            {
                Id = "p-mp3",
                Name = "MP3 car",
                RootPath = "/archive-mp3",
                Format = "mp3:192",
                Enabled = true,
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".archive-card"), Has.Count.EqualTo(1)),
            TimeSpan.FromSeconds(3));
        cut.Find("input[aria-label='Enable MP3 car']").Change(false);

        cut.WaitForAssertion(() =>
        {
            using var db = _factory.CreateDbContext();
            Assert.That(db.ArchiveProfiles.AsNoTracking().Single(p => p.Id == "p-mp3").Enabled,
                Is.False, "the toggle writes through to the store");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void AddLocationModal_ShowsDockerMountWarning()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder").Click();
        // The page initializes asynchronously; under load the modal can
        // land a moment after the click.
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Add scan location")),
            TimeSpan.FromSeconds(5));

        // The shared MountWarning component, same text on every screen.
        Assert.That(cut.Markup, Does.Contain("mount-warning"),
            "the shared warning component renders in the modal");
        Assert.That(cut.Markup, Does.Contain("as the container sees it"),
            "the notice names the container-path rule");
        Assert.That(cut.Markup, Does.Contain("volumes:"),
            "the notice points at the compose volumes section");
        Assert.That(cut.Markup, Does.Contain("zero files"),
            "the notice says what a wrong path costs: a scan that finds nothing");
    }

    [Test]
    public void AddLocationModal_HasScheduleModePicker()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder").Click();
        // The page initializes asynchronously; under the full suite the
        // modal can land a moment after the click.
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Add scan location")),
            TimeSpan.FromSeconds(5));

        // The same six named options everywhere, the sync job wording.
        foreach (var option in new[] { "Manual", "Hourly", "Daily", "Weekly", "Monthly", "Cron" })
            Assert.That(cut.Markup, Does.Contain($">{option}</option>"),
                $"the schedule select offers {option}");
    }

    [Test]
    public void SaveLocation_PersistsScheduleAndLiveMonitoring()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder").Click();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Add scan location")),
            TimeSpan.FromSeconds(5));

        // A real directory: saving refuses paths the app cannot see.
        var dir = Path.Combine(Path.GetTempPath(), $"mb-loc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        // Fill the path.
        cut.Find("input[placeholder='/music']").Change(dir);

        // Pick Hourly in the schedule select (the first select on the
        // modal belongs to SchedulePicker).
        var selects = cut.FindAll("select");
        selects[0].Change("Hourly");
        cut.Render(); // let the picker push the new schedule up

        // Live monitoring is on by default; submit.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder" && b.ClassList.Contains("primary")).Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var saved = db.ScanLocations.Single(l => l.Path == dir);
        Assert.That(saved.ScheduleCron, Is.EqualTo("0 * * * *"),
            "the Hourly preset lands as its cron equivalent");
        Assert.That(saved.LiveMonitoring, Is.True);
    }

    [Test]
    public void SaveLocation_CustomCron_Persists()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("input[placeholder='/music']"), Has.Count.EqualTo(1),
                "the add dialog opens"));

        // A real directory: saving refuses paths the app cannot see.
        var dir = Path.Combine(Path.GetTempPath(), $"mb-loc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        cut.Find("input[placeholder='/music']").Change(dir);

        var selects = cut.FindAll("select");
        selects[0].Change("Cron"); // mode
        cut.Render();

        // Custom mode shows the free-text cron input; commit a real expression.
        cut.Find("input[placeholder='30 4 * * *']").Change("0 22 * * 5");
        cut.Render();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder" && b.ClassList.Contains("primary")).Click();
        cut.Render();

        using var db = _factory.CreateDbContext();
        var saved = db.ScanLocations.Single(l => l.Path == dir);
        Assert.That(saved.ScheduleCron, Is.EqualTo("0 22 * * 5"),
            "the typed cron expression lands in the DB verbatim");
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

    [Test]
    public void SaveLocation_NonExistentPath_ShowsError_AndDoesNotSave()
    {
        var cut = _ctx.Render<Library>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("input[placeholder='/music']"), Has.Count.EqualTo(1),
                "the add dialog opens"));

        // A host-style path the app cannot see from inside its process.
        cut.Find("input[placeholder='/music']").Change("/home/someone/Music/techno");
        cut.Render();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add music folder" && b.ClassList.Contains("primary")).Click();
        cut.Render();

        Assert.That(cut.Markup, Does.Contain("does not exist from the app's point of view"),
            "the inline error names the container-vs-host path trap");
        Assert.That(cut.Markup, Does.Contain("wizard-modal"),
            "the modal stays open so the user can fix the path");

        using var db = _factory.CreateDbContext();
        Assert.That(db.ScanLocations.Count(l => l.Path == "/home/someone/Music/techno"), Is.Zero,
            "a path the app cannot see is never saved");
    }

    [Test]
    public void LocationCard_MissingPath_ShowsErrPill()
    {
        // /music/jazz from Setup does not exist on the test host: the card
        // must say so instead of silently reporting zero files.
        var cut = _ctx.Render<Library>();

        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".location-card").Single(c => c.TextContent.Contains("/music/jazz"));
            Assert.That(card.QuerySelector(".pill.err"), Is.Not.Null,
                "a path the app cannot see gets an explicit missing marker");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task SavingADefaultArchive_RegistersItAsAScanLocation()
    {
        var archiveDir = Path.Combine(Path.GetTempPath(), $"mb-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(archiveDir);
        try
        {
            var cut = _ctx.Render<Library>();
            cut.WaitForAssertion(() => Assert.That(cut.FindAll(".archive-default-row"), Has.Count.EqualTo(1)));

            cut.FindAll(".archive-default-row button")
                .First(b => b.TextContent.Trim() == "Edit").Click();
            cut.WaitForAssertion(() =>
                Assert.That(cut.Markup, Does.Contain("Default archive")));

            cut.Find("input[placeholder='/archive-opus']").Change(archiveDir);
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Save destination").Click();

            cut.WaitForAssertion(() =>
            {
                using var db = _factory.CreateDbContext();
                Assert.That(db.ScanLocations.Any(location => location.Path == archiveDir), Is.True,
                    "the default archive folder joins the scan locations so its files are known copies");
                Assert.That(db.DownloaderSettings.Single(s => s.Key == "archive_path").Value,
                    Is.EqualTo(archiveDir), "the setting itself is stored");
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(archiveDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ClearingADefaultArchive_RemovesItsScanLocation()
    {
        var oldArchive = Path.Combine(Path.GetTempPath(), $"mb-archive-old-{Guid.NewGuid():N}");
        Directory.CreateDirectory(oldArchive);
        try
        {
            using (var db = _factory.CreateDbContext())
            {
                db.DownloaderSettings.Add(new DownloaderSettingEntity
                    { Key = "archive_path", Value = oldArchive });
                db.DownloaderSettings.Add(new DownloaderSettingEntity
                    { Key = "archive_format", Value = "auto" });
                db.ScanLocations.Add(new ScanLocationEntity { Path = oldArchive });
                db.SaveChanges();
            }

            var cut = _ctx.Render<Library>();
            cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain(oldArchive)));

            cut.FindAll(".archive-default-row button")
                .First(b => b.TextContent.Trim() == "Edit").Click();
            cut.Find("input[placeholder='/archive-opus']").Change("");
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Save destination").Click();

            cut.WaitForAssertion(() =>
            {
                using var db = _factory.CreateDbContext();
                Assert.That(db.ScanLocations.Any(location => location.Path == oldArchive), Is.False,
                    "a cleared archive root stops being scanned as music");
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(oldArchive, recursive: true); } catch { /* best effort */ }
        }
    }

}
