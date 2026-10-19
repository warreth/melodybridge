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
            .Setup(d => d.GetUsersAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaServerUserOption>
            {
                new("u1", "Alice"),
                new("u2", "Bob"),
            });
        _userDirectory
            .Setup(d => d.TestConnectionAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsWizard_{Guid.NewGuid()}")
            .Options;
        _dbFactory = new InMemFactory(options);

        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IMediaServerDirectory>(_userDirectory.Object);
        // Kind-based lookup through the factory, like the real app.
        _ctx.Services.AddSingleton<IMediaServerDirectoryFactory>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerDirectoryFactory(
                sp.GetServices<IMediaServerDirectory>()));
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_dbFactory);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory));
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.MediaServerProfileStore(
            new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory)));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginService>(
            new TestServices.StubJellyfinLogin());
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

    // ── C2: media server profiles instead of raw credentials ───────

    /// <summary>Saves a profile through the same store the page reads.</summary>
    private void SeedProfile(string id, string name, string kind, string url, string key)
    {
        var store = new MelodyBridge.Infrastructure.Services.MediaServerProfileStore(
            new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory));
        store.SaveAsync(new MelodyBridge.Infrastructure.Services.MediaServerProfile
        {
            Id = id,
            Name = name,
            Kind = kind,
            BaseUrl = url,
            ApiKey = key,
        }).GetAwaiter().GetResult();
    }

    /// <summary>The target row of a media server profile on the output step.</summary>
    private static AngleSharp.Dom.IElement ProfileRow(IRenderedComponent<SyncJobs> cut, string profileName)
        => cut.FindAll("label.target-option").Single(l => l.TextContent.Contains(profileName));

    /// <summary>Ticks a media server profile as an additional target.</summary>
    private static void TickProfile(IRenderedComponent<SyncJobs> cut, string profileName)
        => ProfileRow(cut, profileName).QuerySelector("input[type='checkbox']")!.Change(true);

    private IRenderedComponent<SyncJobs> OpenOutputStepWithProfile(string profileName)
    {
        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2); // step 3 = targets
        TickProfile(cut, profileName);
        return cut;
    }

    [Test]
    public void Step3_ProfilesAreTickedTargets_AndNeverAskForCredentials()
    {
        SeedProfile("prof-1", "Homelab", "Jellyfin", "http://jf:8096", "token-1");
        var cut = OpenOutputStepWithProfile("Homelab");

        Assert.That(ProfileRow(cut, "Homelab").TextContent, Does.Contain("http://jf:8096"));
        Assert.That(ProfileRow(cut, "Homelab").TextContent, Does.Contain("Jellyfin"),
            "the row names the server kind");
        Assert.That(cut.FindAll(".target-summary").Single().TextContent,
            Does.Contain("M3U file"), "the summary names the chosen targets");
        Assert.That(cut.Markup, Does.Not.Contain("http://host.docker.internal:8096"),
            "the wizard no longer offers a raw server URL");
        Assert.That(cut.Markup, Does.Not.Contain("password (not stored)"),
            "the wizard no longer offers raw credentials");
        Assert.That(cut.Markup, Does.Not.Contain("Sign in"),
            "sign-in moved to the profile editor in Settings");
    }

    [Test]
    public void Step3_TestConnection_UsesTheTickedProfile_AndListsUsers()
    {
        // A Navidrome directory registered after the Jellyfin one: a
        // single IMediaServerDirectory resolve picks the last one, which
        // is the production composition bug this test pins.
        _userDirectory.SetupGet(d => d.Kind).Returns("Jellyfin");
        var navidrome = new Mock<IMediaServerDirectory>();
        navidrome.SetupGet(d => d.Kind).Returns("Navidrome");
        _ctx.Services.AddSingleton<IMediaServerDirectory>(navidrome.Object);

        SeedProfile("prof-1", "Homelab", "Jellyfin", "http://jf:8096", "token-1");
        var cut = OpenOutputStepWithProfile("Homelab");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Test connection").Click();

        _userDirectory.Verify(
            d => d.TestConnectionAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://jf:8096" && c.ApiKey == "token-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _userDirectory.Verify(
            d => d.GetUsersAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://jf:8096" && c.ApiKey == "token-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        navidrome.Verify(
            d => d.TestConnectionAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a Jellyfin target must never be tested with the Navidrome directory");
        Assert.That(cut.Markup, Does.Contain("connected"));
        Assert.That(cut.Markup, Does.Contain("Alice"));
    }

    [Test]
    public void Step3_NoProfile_UntickedM3u_BlocksNext_AndLinksToSettings()
    {
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2); // targets step, no profiles exist

        // Switch the default M3U target off: nothing is left to publish to.
        cut.FindAll("label.target-option")
            .Single(l => l.TextContent.Contains("M3U file"))
            .QuerySelector("input[type='checkbox']")!.Change(false);
        Next(cut);

        Assert.That(cut.Markup, Does.Contain("Step 3 of 5"), "must stay on the targets step");
        Assert.That(cut.Markup, Does.Contain("Settings, Media servers"),
            "the error points at where a profile is created");
        Assert.That(cut.Markup, Does.Contain("href=\"/settings#media-servers\""));
    }

    [Test]
    public void Step3_M3uAndTwoProfiles_SaveStoresEveryTarget()
    {
        // The reported case: one job publishing to an M3U file and two
        // media servers at once.
        SeedProfile("prof-jf", "JF", "Jellyfin", "http://jf:1", "t1");
        SeedProfile("prof-plex", "PX", "Plex", "http://px:1", "t2");

        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2);
        TickProfile(cut, "JF");
        TickProfile(cut, "PX");
        Next(cut); // remaps
        Next(cut); // review

        Assert.That(cut.Markup, Does.Contain("Targets:"),
            "the review names the target set");
        Assert.That(cut.Markup, Does.Contain("Jellyfin (JF)"));
        Assert.That(cut.Markup, Does.Contain("Plex (PX)"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(3), "M3U plus two servers");
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.M3uFile
                && !string.IsNullOrWhiteSpace(t.M3uPath)), Is.True);
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.JellyfinApi
                && t.ProfileId == "prof-jf"), Is.True);
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.PlexApi
                && t.ProfileId == "prof-plex"), Is.True);
            Assert.That(job.MediaServerProfileId, Is.EqualTo("prof-jf"),
                "the legacy mirror keeps the first server profile");
            Assert.That(job.OutputTarget, Is.EqualTo("M3uFile"),
                "the legacy mirror keeps the first target kind");
            Assert.That(job.JellyfinServerUrl, Is.Null, "profiles own the credentials");
        });
    }

    [Test]
    public void Step3_TickedProfileKind_BecomesTheTargetKind()
    {
        SeedProfile("prof-plex", "Plexy", "Plex", "http://px:1", "t2");
        SeedProfile("prof-sub", "Navvy", "Navidrome", "http://nv:1", "t3");

        SeedScanLocations("/music");
        var cut = OpenWizardWithPlaylistSource();
        Next(cut, 2);
        TickProfile(cut, "Plexy");
        Next(cut);
        Next(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job)
                .Any(t => t.Kind == OutputTargetType.PlexApi && t.ProfileId == "prof-plex"),
            Is.True, "a Plex profile becomes a Plex target");

        // And the Subsonic kind maps from a Navidrome profile.
        using (var db2 = _dbFactory.CreateDbContext())
        {
            foreach (var row in db2.SyncJobs) db2.SyncJobs.Remove(row);
            db2.SaveChanges();
        }
        var cut2 = _ctx.Render<SyncJobs>();
        cut2.FindAll("button").First(b => b.TextContent.Trim() == "New sync job").Click();
        cut2.FindAll("select")[1].Change("p1");
        Next(cut2, 2);
        TickProfile(cut2, "Navvy");
        Next(cut2);
        Next(cut2);
        cut2.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db3 = _dbFactory.CreateDbContext();
        var job2 = db3.SyncJobs.AsNoTracking().Single();
        Assert.That(MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job2)
                .Any(t => t.Kind == OutputTargetType.SubsonicApi && t.ProfileId == "prof-sub"),
            Is.True, "Navidrome profiles speak the Subsonic API");
    }

    [Test]
    public void Step3_LegacyJob_KeepsItsConnectionTestable_AndItsUserPickable()
    {
        _userDirectory.SetupGet(d => d.Kind).Returns("Jellyfin");
        SeedPlaylist("p1", "My List");
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-legacy",
                Name = "Legacy",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "JellyfinApi",
                JellyfinServerUrl = "http://old:8096",
                JellyfinApiKey = "old-key",
                JellyfinUserId = "u2",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Next(cut, 2);

        // The job's own connection stays a ticked target, testable, and the
        // Jellyfin user stays pickable, until a profile replaces it.
        Assert.That(cut.Markup, Does.Contain("saved in this job"),
            "the legacy target names itself");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Test connection").Click();
        _userDirectory.Verify(
            d => d.TestConnectionAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://old:8096" && c.ApiKey == "old-key"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(cut.Markup, Does.Contain("Jellyfin user"));
        Assert.That(cut.Markup, Does.Contain("Bob"), "the user list renders for the legacy job");
        Assert.That(cut.Markup, Does.Not.Contain("No media server profiles yet"),
            "the legacy connection is a usable path, not a missing-profile dead end");
    }

    [Test]
    public void Step3_JobWhoseProfileWasDeleted_PrunesItAndSavesAReplacement()
    {
        SeedProfile("prof-dead", "Gone", "Jellyfin", "http://dead:1", "t1");
        SeedProfile("prof-live", "Live", "Jellyfin", "http://live:1", "t2");
        SeedPlaylist("p1", "My List");
        using (var db = _dbFactory.CreateDbContext())
        {
            var job = new SyncJobEntity
            {
                Id = "job-stale-profile",
                Name = "Stale profile",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                Schedule = "Manual",
            };
            MelodyBridge.Infrastructure.Services.SyncJobMapping.ApplyTargets(job, new[]
            {
                new SyncTarget(OutputTargetType.M3uFile, M3uPath: "/out/stale.m3u"),
                new SyncTarget(OutputTargetType.JellyfinApi, ProfileId: "prof-dead"),
            });
            db.SyncJobs.Add(job);
            db.SaveChanges();
        }
        var store = new MelodyBridge.Infrastructure.Services.MediaServerProfileStore(
            new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory));
        Assert.That(store.DeleteAsync("prof-dead").GetAwaiter().GetResult(), Is.True);

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Next(cut, 2);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("profile no longer exists"),
                "the wizard explains why the target is gone");
            Assert.That(cut.Markup, Does.Not.Contain("http://dead:1"),
                "the deleted profile has no row to tick");
        });

        TickProfile(cut, "Live");
        Next(cut); // remaps
        Next(cut); // review
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        using var db2 = _dbFactory.CreateDbContext();
        var saved = db2.SyncJobs.AsNoTracking().Single(j => j.Id == "job-stale-profile");
        var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(saved);
        Assert.Multiple(() =>
        {
            Assert.That(targets.Any(t => t.ProfileId == "prof-dead"), Is.False,
                "the stale target is dropped on save");
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.JellyfinApi
                && t.ProfileId == "prof-live"), Is.True, "the replacement target is stored");
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.M3uFile
                && t.M3uPath == "/out/stale.m3u"), Is.True, "the other targets survive");
        });
    }

    [Test]
    public void Step3_LegacyJob_SaveKeepsItsSavedConnectionTarget()
    {
        SeedScanLocations("/music");
        SeedPlaylist("p1", "My List");
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-legacy-save",
                Name = "Legacy save",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "JellyfinApi",
                JellyfinServerUrl = "http://old:8096",
                JellyfinApiKey = "old-key",
                JellyfinUserId = "u2",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Next(cut, 2);
        Assert.That(cut.Markup, Does.Contain("saved in this job"));
        Next(cut); // remaps
        Next(cut); // review
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        using var db2 = _dbFactory.CreateDbContext();
        var job = db2.SyncJobs.AsNoTracking().Single(j => j.Id == "job-legacy-save");
        var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(1), "the legacy target survives an edit");
            Assert.That(targets[0].Kind, Is.EqualTo(OutputTargetType.JellyfinApi));
            Assert.That(targets[0].ProfileId, Is.Null, "it stays the job's own connection");
            Assert.That(job.JellyfinServerUrl, Is.EqualTo("http://old:8096"));
            Assert.That(job.JellyfinApiKey, Is.EqualTo("old-key"));
            Assert.That(job.JellyfinUserId, Is.EqualTo("u2"));
        });
    }

    [Test]
    public void Step3_LegacyJob_ReplacingTheSavedConnectionWithAProfile_ClearsTheInlineValues()
    {
        SeedProfile("prof-1", "Homelab", "Jellyfin", "http://jf:8096", "token-1");
        SeedScanLocations("/music");
        SeedPlaylist("p1", "My List");
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-legacy-replace",
                Name = "Legacy replace",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "JellyfinApi",
                JellyfinServerUrl = "http://old:8096",
                JellyfinApiKey = "old-key",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        Next(cut, 2);

        // Untick the saved connection, tick the profile, save.
        cut.FindAll("label.target-option")
            .Single(l => l.TextContent.Contains("saved in this job"))
            .QuerySelector("input[type='checkbox']")!.Change(false);
        TickProfile(cut, "Homelab");
        Next(cut);
        Next(cut);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        using var db2 = _dbFactory.CreateDbContext();
        var job = db2.SyncJobs.AsNoTracking().Single(j => j.Id == "job-legacy-replace");
        var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job);
        Assert.Multiple(() =>
        {
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.JellyfinApi
                && t.ProfileId == "prof-1"), Is.True);
            Assert.That(targets.Any(t => t.Kind == OutputTargetType.JellyfinApi
                && t.ProfileId is null), Is.False, "the replaced connection target is gone");
            Assert.That(job.JellyfinServerUrl, Is.Null, "the inline credentials are cleared");
            Assert.That(job.JellyfinApiKey, Is.Null);
        });
    }

    [Test]
    public void Step3_M3uPath_PrefilledFromThePlaylistOutputFolderSetting()
    {
        new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory)
            .SetAsync("playlist_path", "/srv/playlists").GetAwaiter().GetResult();
        SeedScanLocations("/music");
        SeedPlaylist("p1", "My List");
        var cut = OpenWizard();
        cut.FindAll("select")[1].Change("p1"); // the source playlist select
        Next(cut, 2);

        var value = cut.Find("input[placeholder='/app/playlists/playlist.m3u']").GetAttribute("value");
        Assert.That(value, Is.EqualTo("/srv/playlists/My List.m3u"),
            "the Playlist output folder setting names the folder, the job name the file");
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
                    "Some song: no local file",
                }),
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Log").Click();

        Assert.That(cut.Markup, Does.Contain("Logged job"));
        Assert.That(cut.Markup, Does.Contain("Synced 2/3 tracks"));
        Assert.That(cut.Markup, Does.Contain("1 warning"));
        Assert.That(cut.Markup, Does.Contain("Some song: no local file"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
