using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Wizard behaviour end to end: checkbox scan locations (C1), Jellyfin
/// user picker (C2), multiple remap rules (C3), cron schedule (C4) and
/// the local-folder source (C5). Asserts rendered markup and real DB
/// rows written through the page's own DbContextFactory.
/// </summary>
[TestFixture]
[Category("UI")]
public class SyncJobsWizardUiTests
{
    private TestContext _ctx = null!;
    private Mock<ISyncJobRunner> _jobRunner = null!;
    private Mock<IMediaServerDirectory> _userDirectory = null!;
    private InMemFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _jobRunner = new Mock<ISyncJobRunner>();
        _userDirectory = new Mock<IMediaServerDirectory>();
        _userDirectory
            .Setup(d => d.GetUsersAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaServerUserOption>
            {
                new("u1", "Alice"),
                new("u2", "Bob"),
            });
        _userDirectory
            .Setup(d => d.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsWizard_{Guid.NewGuid()}")
            .Options;
        _dbFactory = new InMemFactory(options);

        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IMediaServerDirectory>(_userDirectory.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private void SeedScanLocations(params string[] paths)
    {
        using var db = _dbFactory.CreateDbContext();
        foreach (var p in paths)
            db.ScanLocations.Add(new ScanLocationEntity { Path = p });
        db.SaveChanges();
    }

    private void SeedPlaylist(string id, string name)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity { Id = id, Name = name, SourceUrl = "stub:" + id });
        db.SaveChanges();
    }

    /// <summary>Opens the wizard and picks the seeded playlist on step 1.</summary>
    private IRenderedComponent<SyncJobs> OpenWizardWithPlaylistSource()
    {
        SeedPlaylist("p1", "My List");
        var cut = OpenWizard();
        cut.Find("select").Change("p1");
        return cut;
    }

    private IRenderedComponent<SyncJobs> OpenWizard()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        return cut;
    }

    private static void Next(IRenderedComponent<SyncJobs> cut, int times = 1)
    {
        for (var i = 0; i < times; i++)
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
    }

    /// <summary>From any early step: walk forward until the output step,
    /// fill the required M3U path, then continue to the review step.</summary>
    private static void WalkToReview(IRenderedComponent<SyncJobs> cut)
    {
        // step over name/source and locations until the M3U path input shows
        for (var guard = 0; guard < 4; guard++)
        {
            if (cut.FindAll("input[placeholder='/app/playlists/playlist.m3u']").Count > 0)
                break;
            Next(cut);
        }
        cut.Find("input[placeholder='/app/playlists/playlist.m3u']")
            .Change("/app/playlists/out.m3u");
        Next(cut, 2); // -> remaps -> review
    }

    // ── C1: checkbox list of scanner folders ────────────────────────

    [Test]
    public void Step2_ShowsCheckboxPerScanLocation_NoneCheckedByDefault()
    {
        SeedScanLocations("/music/a", "/music/b", "/music/c");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut);

        var checkboxes = cut.FindAll("input[type='checkbox']");
        Assert.That(checkboxes, Has.Count.EqualTo(3),
            "one checkbox per scan location");
        Assert.That(checkboxes.All(c => !c.HasAttribute("checked")), Is.True,
            "no folder is checked by default");
        Assert.That(cut.FindAll("label.toggle-switch.wide"), Has.Count.EqualTo(3),
            "folder rows use the wide toggle switch markup");
    }

    [Test]
    public void Step2_EmptyLocations_ShowsHintWithLibraryLink()
    {
        var cut = OpenWizardWithPlaylistSource();
        Next(cut);
        Assert.That(cut.Markup, Does.Contain("No scan locations yet"));
        Assert.That(cut.Markup, Does.Contain("href=\"/library\""));
    }

    [Test]
    public void Step2_UncheckOne_SaveStoresOnlyCheckedPaths()
    {
        SeedScanLocations("/music/a", "/music/b", "/music/c");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut);

        // Check every folder except /music/b
        foreach (var folder in new[] { "/music/a", "/music/c" })
        {
            cut.FindAll("label.toggle-switch.wide")
                .Single(l => l.TextContent.Contains(folder))
                .QuerySelector("input[type='checkbox']").Change(true);
        }

        Next(cut); // -> output step
        WalkToReview(cut);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.EqualTo(new[] { "/music/a", "/music/c" }),
            "only the checked folders must be stored");
    }

    [Test]
    public void Step2_NoneChecked_EmptySelectionSavesAsNoFilter()
    {
        SeedScanLocations("/music/a", "/music/b");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut); // locations step, nothing checked
        Next(cut); // empty is valid: no folder filter
        WalkToReview(cut);

        Assert.That(cut.Markup, Does.Contain("Every folder"),
            "the review must state the empty selection means every folder");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.Empty,
            "empty selection persists as an empty list (runner: no filter)");
    }

    [Test]
    public void EditHydration_StoresOnlyStoredPathsChecked()
    {
        SeedScanLocations("/a", "/b");
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-h",
                Name = "Hydrate me",
                SourceId = "p-any",
                SearchLocationPaths = System.Text.Json.JsonSerializer.Serialize(new List<string> { "/a" }),
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out/x.m3u",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Next(cut); // step 2

        var labels = cut.FindAll("label.toggle-switch.wide");
        Assert.That(labels.Count(l => l.QuerySelector("input[type='checkbox']")!.HasAttribute("checked")
             && l.TextContent.Contains("/a")), Is.EqualTo(1), "/a is checked");
        Assert.That(labels.Count(l => l.QuerySelector("input[type='checkbox']")!.HasAttribute("checked")
             && l.TextContent.Contains("/b")), Is.EqualTo(0), "/b is not checked");
    }

    // ── C2: Jellyfin user picker ────────────────────────────────────

    [Test]
    public void Step3_JellyfinBranch_ShowsUserDropdownAndTestButton()
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2); // step 3 = output
        cut.FindAll("select").First(s => s.TextContent.Contains("M3U File")).Change("JellyfinApi");

        Assert.That(cut.Markup, Does.Contain("(server default)"));
        Assert.That(cut.Markup, Does.Contain("Test connection"));
    }

    [Test]
    public void Step3_TestConnection_CallsDirectoryWithWizardValues_AndListsUsers()
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2);
        cut.FindAll("select").First(s => s.TextContent.Contains("M3U File")).Change("JellyfinApi");

        cut.Find("input[placeholder='http://host.docker.internal:8096']").Change("http://jf:8096");
        cut.Find("input[placeholder='API key']").Change("key-1");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Test connection").Click();

        _userDirectory.Verify(
            d => d.TestConnectionAsync("http://jf:8096", "key-1", It.IsAny<CancellationToken>()),
            Times.Once);
        _userDirectory.Verify(
            d => d.GetUsersAsync("http://jf:8096", "key-1", It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(cut.Markup, Does.Contain("reachable"));
        Assert.That(cut.Markup, Does.Contain("Alice"));
    }

    // ── C3: multiple remap rules ────────────────────────────────────

    [Test]
    public void Step4_TwoPathRulesAndOneExtRule_SaveStoresDictionaries()
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut); // -> output step
        WalkToReview(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Back").Click(); // remaps


        // two path rules (rows use @oninput, so drive them with Input())
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add path rule").Click();
        cut.FindAll("input[placeholder='/mnt/BackupHDD/Music']")[0].Input("/from/one");
        cut.FindAll("input[placeholder='/media/music']")[0].Input("/to/one");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add path rule").Click();
        cut.FindAll("input[placeholder='/mnt/BackupHDD/Music']")[1].Input("/from/two");
        cut.FindAll("input[placeholder='/media/music']")[1].Input("/to/two");

        // one extension rule
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add extension rule").Click();
        cut.FindAll("input[placeholder='.flac']")[0].Input(".flac");
        cut.FindAll("input[placeholder='.opus']")[0].Input(".opus");

        Next(cut); // review
        Assert.That(cut.Markup, Does.Contain("/from/one"));
        Assert.That(cut.Markup, Does.Contain("/from/two"));
        Assert.That(cut.Markup, Does.Contain(".flac"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var paths = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(job.PathRemapRules);
        Assert.That(paths, Is.EqualTo(new Dictionary<string, string>
        {
            ["/from/one"] = "/to/one",
            ["/from/two"] = "/to/two",
        }));
        var exts = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string>>(job.ExtensionRemapRules);
        Assert.That(exts, Is.EqualTo(new Dictionary<string, string> { [".flac"] = ".opus" }));
    }

    [Test]
    public void EditHydration_RendersTwoRuleRows_FromStoredDictionary()
    {
        SeedPlaylist("p1", "My List");
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-r",
                Name = "Rules",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out/x.m3u",
                PathRemapRules = System.Text.Json.JsonSerializer.Serialize(
                    new Dictionary<string, string>
                    {
                        ["/old"] = "/new",
                        ["/alt"] = "/other",
                    }),
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Next(cut); // -> output step
        WalkToReview(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Back").Click(); // remaps

        var fromInputs = cut.FindAll("input[placeholder='/mnt/BackupHDD/Music']");
        Assert.That(fromInputs, Has.Count.EqualTo(2), "two stored path rules render as two rows");
        Assert.That(fromInputs.Any(i => (i.GetAttribute("value") ?? "") == "/old"), Is.True);
        Assert.That(fromInputs.Any(i => (i.GetAttribute("value") ?? "") == "/alt"), Is.True);
    }

    // ── C4: cron schedule ──────────────────────────────────────────

    [Test]
    public void Step3_Cron_ShowsExpressionInput()
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2);
        cut.FindAll("select").First(s => s.TextContent.Contains("Manual")).Change("Cron");

        Assert.That(cut.Markup, Does.Contain("placeholder=\"0 3 * * *\""));
    }

    [Test]
    public void Step3_Cron_BlankExpression_BlocksNext_WithMessage()
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2);
        // satisfy the M3U path first so only the cron rule can block
        cut.Find("input[placeholder='/app/playlists/playlist.m3u']")
            .Change("/app/playlists/out.m3u");
        cut.FindAll("select").First(s => s.TextContent.Contains("Manual")).Change("Cron");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        Assert.That(cut.Markup, Does.Contain("Step 3 of 5"),
            "must stay on the output step");
        Assert.That(cut.Markup, Does.Contain("cron expression"),
            "a validation message must tell what is missing");
    }

    [Test]
    public void Step3_Cron_ValidExpression_PassesToReview_AndSaves()
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2);
        cut.FindAll("select").First(s => s.TextContent.Contains("Manual")).Change("Cron");
        cut.Find("input[placeholder='0 3 * * *']").Change("15 2 * * 6");

        WalkToReview(cut);
        Assert.That(cut.Markup, Does.Contain("Step 5 of 5"));
        Assert.That(cut.Markup, Does.Contain("15 2 * * 6"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(job.Schedule, Is.EqualTo("Cron"));
        Assert.That(job.CronExpression, Is.EqualTo("15 2 * * 6"));
    }

    // ── C5: local folder source ─────────────────────────────────────

    [Test]
    public void Step1_LocalFolderChoice_SavesSourceIdNullAndFolderInSearchPaths()
    {
        SeedScanLocations("/music/folder");
        var cut = OpenWizard();
        cut.Find("select").Change("folder");
        cut.FindAll("select").Last().Change("/music/folder");

        Next(cut); // -> locations step (all checked)
        Next(cut); // -> output step
        WalkToReview(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(job.SourceId, Is.Null, "folder jobs have no playlist source");
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.EqualTo(new[] { "/music/folder" }),
            "the chosen folder is the search location list");
    }

    [Test]
    public void Step1_NoSourceSelected_BlocksNext_WithMessage()
    {
        var cut = OpenWizard();
        Next(cut);
        Assert.That(cut.Markup, Does.Contain("Step 1 of 5"));
        Assert.That(cut.Markup, Does.Contain("Choose a folder or a playlist first"));
    }

    [Test]
    public void Step1_LocalFolder_NoFolderChosen_BlocksNext()
    {
        SeedScanLocations("/music");
        var cut = OpenWizard();
        cut.Find("select").Change("folder");
        Next(cut);
        Assert.That(cut.Markup, Does.Contain("Choose a folder or a playlist first"));
    }

    [Test]
    public void Step1_LocalFolder_Step2PreChecksOnlyTheChosenFolder()
    {
        SeedScanLocations("/music/folder", "/music/other");
        var cut = OpenWizard();
        cut.Find("select").Change("folder");
        cut.FindAll("select").Last().Change("/music/folder");
        Next(cut); // -> locations step

        var labels = cut.FindAll("label.toggle-switch.wide");
        Assert.That(labels.Count(l => l.QuerySelector("input[type='checkbox']")!.HasAttribute("checked")
             && l.TextContent.Contains("/music/folder")), Is.EqualTo(1),
            "the chosen folder is pre-checked");
        Assert.That(labels.Count(l => l.QuerySelector("input[type='checkbox']")!.HasAttribute("checked")
             && l.TextContent.Contains("/music/other")), Is.EqualTo(0),
            "no other folder is pre-checked");
    }

    // ── C6: run log with warnings breakdown ─────────────────────────

    [Test]
    public void LogModal_RendersWarningDetails()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-l",
                Name = "Logged job",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out.m3u",
                Schedule = "Manual",
                LastRunStatus = "Completed",
            });
            db.SyncJobRuns.Add(new SyncJobRunEntity
            {
                SyncJobId = "job-l",
                Status = "Completed",
                Message = "Synced 2/3 tracks, 1 without a local file",
                ResolvedTracks = 2,
                TotalTracks = 3,
                WarningDetails = System.Text.Json.JsonSerializer.Serialize(new List<string>
                {
                    "Some song — no local file",
                }),
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Log").Click();

        Assert.That(cut.Markup, Does.Contain("Logged job"));
        Assert.That(cut.Markup, Does.Contain("Synced 2/3 tracks"));
        Assert.That(cut.Markup, Does.Contain("1 warning"));
        Assert.That(cut.Markup, Does.Contain("Some song — no local file"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
