using AngleSharp.Dom;
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
/// The single sync job editor end to end: folder search locations (C1),
/// the Jellyfin user picker (C2), multiple remap rules (C3), cron
/// schedules (C4) and the local-folder source (C5). Asserts rendered
/// markup and real DB rows written through the page's own factory.
/// </summary>
[TestFixture]
[Category("UI")]
public class SyncJobsEditorUiTests
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
            .UseInMemoryDatabase($"SyncJobsEditor_{Guid.NewGuid()}")
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
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.SyncRuleProfileStore(_dbFactory));
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
        if (db.Playlists.Any(p => p.Id == id)) return;
        db.Playlists.Add(new PlaylistEntity { Id = id, Name = name, SourceUrl = "stub:" + id });
        db.SaveChanges();
    }

    private IRenderedComponent<SyncJobs> OpenEditor()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        return cut;
    }

    private IRenderedComponent<SyncJobs> OpenEditorWithPlaylistSource()
    {
        SeedPlaylist("p1", "My List");
        var cut = OpenEditor();
        var source = cut.FindAll("select")
            .Single(s => s.QuerySelectorAll("option")
                .Any(o => o.GetAttribute("value") == "p1"));
        source.Change("p1");
        return cut;
    }

    private static void Save(IRenderedComponent<SyncJobs> cut)
        => cut.FindAll("button").First(b => b.TextContent.Trim() is "Create sync job" or "Save changes").Click();

    private static IElement FolderToggle(IRenderedComponent<SyncJobs> cut, string folder)
        => cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains(folder));

    // -- C1: checkbox list of scanner folders -----------------------

    [Test]
    public void FolderSearch_ShowsCheckboxPerScanLocation_NoneCheckedByDefault()
    {
        SeedScanLocations("/music/a", "/music/b", "/music/c");
        var cut = OpenEditorWithPlaylistSource();

        var details = cut.Find("details.quick-folders");
        var checkboxes = details.QuerySelectorAll(".toggle-switch.wide input[type='checkbox']");
        Assert.That(checkboxes, Has.Count.EqualTo(3),
            "one checkbox per scan location");
        Assert.That(checkboxes.All(c => !c.HasAttribute("checked")), Is.True,
            "no folder is checked by default");
        Assert.That(details.TextContent, Does.Contain("Search folders (all)"));
    }

    [Test]
    public void FolderSource_EmptyLocations_ShowsHintWithLibraryLink()
    {
        var cut = OpenEditor();
        cut.FindAll("select").First(s => s.TextContent.Contains("Local folder")).Change("folder");

        Assert.That(cut.Markup, Does.Contain("No scan locations yet"));
        Assert.That(cut.Markup, Does.Contain("href=\"/library\""));
    }

    [Test]
    public void FolderSearch_UncheckOne_SaveStoresOnlyCheckedPaths()
    {
        SeedScanLocations("/music/a", "/music/b", "/music/c");
        var cut = OpenEditorWithPlaylistSource();

        FolderToggle(cut, "/music/a").QuerySelector("input[type='checkbox']")!.Change(true);
        FolderToggle(cut, "/music/c").QuerySelector("input[type='checkbox']")!.Change(true);
        Save(cut);

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.EqualTo(new[] { "/music/a", "/music/c" }),
            "only the checked folders must be stored");
    }

    [Test]
    public void FolderSearch_NoneChecked_EmptySelectionSavesAsNoFilter()
    {
        SeedScanLocations("/music/a", "/music/b");
        var cut = OpenEditorWithPlaylistSource();
        Save(cut);

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
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        Assert.That(FolderToggle(cut, "/a").QuerySelector("input[type='checkbox']")!.HasAttribute("checked"),
            Is.True, "/a is checked");
        Assert.That(FolderToggle(cut, "/b").QuerySelector("input[type='checkbox']")!.HasAttribute("checked"),
            Is.False, "/b is not checked");
    }

    // -- C2: media server profiles instead of raw credentials ------

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

    private static void TickProfile(IRenderedComponent<SyncJobs> cut, string profileName)
        => cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains(profileName))
            .QuerySelector("input[type='checkbox']")!.Change(true);

    [Test]
    public void Targets_ProfilesAreTicked_AndNeverAskForCredentials()
    {
        SeedProfile("prof-1", "Homelab", "Jellyfin", "http://jf:8096", "token-1");
        var cut = OpenEditorWithPlaylistSource();
        TickProfile(cut, "Homelab");

        var row = cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Homelab"));
        Assert.That(row.TextContent, Does.Contain("Jellyfin"), "the row names the server kind");
        Assert.That(cut.Markup, Does.Not.Contain("http://host.docker.internal:8096"),
            "the editor never offers a raw server URL");
        Assert.That(cut.Markup, Does.Not.Contain("password (not stored)"),
            "the editor never offers raw credentials");

        Save(cut);
        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job)
                .Any(t => t.Kind == OutputTargetType.JellyfinApi && t.ProfileId == "prof-1"),
            Is.True);
    }

    [Test]
    public void Targets_NoProfileAndM3uOff_ShowsErrorAndSettingsLink()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("M3U file"))
            .QuerySelector("input[type='checkbox']")!.Change(false);
        Save(cut);

        Assert.That(cut.Markup, Does.Contain("Pick at least one target"));
        Assert.That(cut.Markup, Does.Contain("Settings, Media servers"),
            "the empty profile list points at where a profile is created");
        Assert.That(cut.Markup, Does.Contain("href=\"/settings#media-servers\""));
    }

    [Test]
    public void Targets_M3uAndTwoProfiles_SaveStoresEveryTarget()
    {
        SeedProfile("prof-jf", "JF", "Jellyfin", "http://jf:1", "t1");
        SeedProfile("prof-plex", "PX", "Plex", "http://px:1", "t2");

        var cut = OpenEditorWithPlaylistSource();
        TickProfile(cut, "JF");
        TickProfile(cut, "PX");
        Save(cut);

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
    public void Targets_TickedProfileKind_BecomesTheTargetKind()
    {
        SeedProfile("prof-plex", "Plexy", "Plex", "http://px:1", "t2");
        SeedProfile("prof-sub", "Navvy", "Navidrome", "http://nv:1", "t3");

        var cut = OpenEditorWithPlaylistSource();
        TickProfile(cut, "Plexy");
        Save(cut);

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job)
                .Any(t => t.Kind == OutputTargetType.PlexApi && t.ProfileId == "prof-plex"),
            Is.True, "a Plex profile becomes a Plex target");

        var cut2 = OpenEditorWithPlaylistSource();
        TickProfile(cut2, "Navvy");
        Save(cut2);

        using var db2 = _dbFactory.CreateDbContext();
        var job2 = db2.SyncJobs.AsNoTracking().Single(j => j.Id != job.Id);
        Assert.That(MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job2)
                .Any(t => t.Kind == OutputTargetType.SubsonicApi && t.ProfileId == "prof-sub"),
            Is.True, "Navidrome profiles speak the Subsonic API");
    }

    [Test]
    public void Targets_LegacyJob_AdvancedKeepsItsConnectionAndUserPicker()
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
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        var advanced = cut.Find("details.quick-advanced");
        var keep = advanced.QuerySelectorAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Keep the legacy connection"));
        Assert.That(keep.QuerySelector("input[type='checkbox']")!.HasAttribute("checked"), Is.True,
            "the saved connection stays ticked until a profile replaces it");
        advanced.QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Test connection").Click();
        cut.WaitForAssertion(() => Assert.That(advanced.TextContent, Does.Contain("Bob")),
            TimeSpan.FromSeconds(3));
        Assert.That(advanced.TextContent, Does.Contain("Jellyfin user"));

        Save(cut);
        using var db2 = _dbFactory.CreateDbContext();
        var saved = db2.SyncJobs.AsNoTracking().Single(j => j.Id == "job-legacy");
        var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(saved);
        Assert.That(targets.Single().Kind, Is.EqualTo(OutputTargetType.JellyfinApi));
        Assert.That(targets.Single().ProfileId, Is.Null, "it stays the job's own connection");
    }

    [Test]
    public void Targets_LegacyJob_ReplacingTheSavedConnectionWithAProfile_ClearsTheInlineValues()
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
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        cut.Find("details.quick-advanced")
            .QuerySelectorAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Keep the legacy connection"))
            .QuerySelector("input[type='checkbox']")!.Change(false);
        TickProfile(cut, "Homelab");
        Save(cut);

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
    public void Targets_JobWhoseProfileWasDeleted_PrunesItAndSavesAReplacement()
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
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("profile no longer"),
                "the editor explains why the target is gone");
            Assert.That(cut.Markup, Does.Not.Contain("http://dead:1"),
                "the deleted profile has no row to tick");
        });

        TickProfile(cut, "Live");
        Save(cut);

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
    public void Targets_M3uPath_PrefilledFromThePlaylistOutputFolderSetting()
    {
        new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory)
            .SetAsync("playlist_path", "/srv/playlists").GetAwaiter().GetResult();
        SeedScanLocations("/music");
        SeedPlaylist("p1", "My List");
        var cut = OpenEditor();
        cut.FindAll("select")
            .Single(s => s.QuerySelectorAll("option")
                .Any(o => o.GetAttribute("value") == "p1"))
            .Change("p1");
        cut.Find("input[placeholder='e.g. My Summer Hits']").Input("My List");

        var value = cut.Find("input[placeholder='/app/playlists/playlist.m3u8']").GetAttribute("value");
        Assert.That(value, Is.EqualTo("/srv/playlists/My List.m3u8"),
            "the Playlist output folder setting names the folder, the job name the file");
    }

    [Test]
    public void Targets_M3uPath_PrefillAvoidsAnExistingJobPath()
    {
        new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory)
            .SetAsync("playlist_path", "/srv/playlists").GetAwaiter().GetResult();
        SeedScanLocations("/music");
        SeedPlaylist("p1", "My List");
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-existing",
                Name = "Existing",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/srv/playlists/My List.m3u8",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = OpenEditor();
        cut.FindAll("select")
            .Single(s => s.QuerySelectorAll("option")
                .Any(o => o.GetAttribute("value") == "p1"))
            .Change("p1");
        cut.Find("input[placeholder='e.g. My Summer Hits']").Input("My List");

        var value = cut.Find("input[placeholder='/app/playlists/playlist.m3u8']").GetAttribute("value");
        Assert.That(value, Is.EqualTo("/srv/playlists/My List (2).m3u8"),
            "the prefill does not reuse a path another job already writes");
    }

    // -- C3: multiple remap rules -----------------------------------

    [Test]
    public void Rules_CustomJob_StartsWithoutRules_AndHidesThemUnderAdvanced()
    {
        var cut = OpenEditorWithPlaylistSource();
        var advanced = cut.Find("details.quick-advanced");
        Assert.That(advanced.HasAttribute("open"), Is.False,
            "advanced options start collapsed for a first setup");
    }

    [Test]
    public void Rules_TwoPathRulesAndOneExtRule_SaveStoresDictionaries()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.Find("details.quick-advanced").SetAttribute("open", "open");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add path rule").Click();
        cut.FindAll("input[placeholder='/music']")[0].Input("/from/one");
        cut.FindAll("input[placeholder='/media/music']")[0].Input("/to/one");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add path rule").Click();
        cut.FindAll("input[placeholder='/music']")[1].Input("/from/two");
        cut.FindAll("input[placeholder='/media/music']")[1].Input("/to/two");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add extension rule").Click();
        cut.FindAll("input[placeholder='.flac']")[0].Input(".flac");
        cut.FindAll("input[placeholder='.mp3']")[0].Input(".opus");

        Save(cut);

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
    public void Rules_EditHydration_RendersTwoRuleRows_FromStoredDictionary()
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
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.Find("details.quick-advanced").SetAttribute("open", "open");

        var fromInputs = cut.FindAll("input[placeholder='/music']");
        Assert.That(fromInputs, Has.Count.EqualTo(2), "two stored path rules render as two rows");
        Assert.That(fromInputs.Any(i => (i.GetAttribute("value") ?? "") == "/old"), Is.True);
        Assert.That(fromInputs.Any(i => (i.GetAttribute("value") ?? "") == "/alt"), Is.True);
    }

    [Test]
    public void Rules_ProfileLinked_OffersTheProfileInsteadOfCustomRules()
    {
        var profile = new MelodyBridge.Core.SyncRuleProfile
        {
            Id = "profile-rules",
            Name = "Living room",
            M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{job}.m3u8",
        };
        new MelodyBridge.Infrastructure.Services.SyncRuleProfileStore(_dbFactory)
            .SaveAsync(profile).GetAwaiter().GetResult();

        var cut = OpenEditorWithPlaylistSource();
        cut.Find("select[aria-label='Rule profile']").Change(profile.Id);

        Assert.That(cut.Find("details.quick-advanced").TextContent,
            Does.Contain("Path and extension rules come from Living room"));
        Assert.That(cut.Markup, Does.Not.Contain("Add path rule"),
            "a linked profile replaces the per-job remap editors");
    }

    // -- C4: cron schedule ------------------------------------------

    [Test]
    public void Schedule_Cron_ShowsExpressionInput()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.FindAll("select").First(s => s.TextContent.Contains("Manual")).Change("Cron");

        Assert.That(cut.Markup, Does.Contain("placeholder=\"0 3 * * *\""));
    }

    [Test]
    public void Schedule_Cron_BlankExpression_BlocksSave_WithMessage()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.FindAll("select").First(s => s.TextContent.Contains("Manual")).Change("Cron");
        Assert.That(cut.FindAll("input[placeholder='0 3 * * *']"), Has.Count.EqualTo(1),
            "the cron input appears after choosing Cron");
        Save(cut);

        Assert.That(cut.FindAll(".job-quick-modal"), Has.Count.EqualTo(1),
            "a blank cron keeps the editor open");
        Assert.That(cut.Markup, Does.Contain("valid five-field cron expression"),
            cut.Find(".job-quick-modal").TextContent);
        using var db = _dbFactory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.Zero, "nothing is saved while the schedule is invalid");
    }

    [Test]
    public void Schedule_Cron_ValidExpression_Saves()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.FindAll("select").First(s => s.TextContent.Contains("Manual")).Change("Cron");
        cut.Find("input[placeholder='0 3 * * *']").Change("15 2 * * 6");
        Save(cut);

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(job.Schedule, Is.EqualTo("Cron"));
        Assert.That(job.CronExpression, Is.EqualTo("15 2 * * 6"));
    }

    // -- C5: local folder source ------------------------------------

    [Test]
    public void FolderSource_ChoiceSavesSourceIdNullAndTheFolder()
    {
        SeedScanLocations("/music/folder");
        var cut = OpenEditor();
        cut.FindAll("select").First(s => s.TextContent.Contains("Local folder")).Change("folder");
        cut.FindAll("select").First(s => s.TextContent.Contains("/music/folder")).Change("/music/folder");
        Save(cut);

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        Assert.That(job.SourceId, Is.Null, "folder jobs have no playlist source");
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.EqualTo(new[] { "/music/folder" }),
            "the chosen folder is the search location list");
    }

    [Test]
    public void FolderSource_NoSourceSelected_BlocksSave()
    {
        var cut = OpenEditor();
        Save(cut);

        Assert.That(cut.Markup, Does.Contain("Choose a playlist first"));
        using var db = _dbFactory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.Zero);
    }

    [Test]
    public void FolderSource_NoFolderChosen_BlocksSave()
    {
        SeedScanLocations("/music");
        var cut = OpenEditor();
        cut.FindAll("select").First(s => s.TextContent.Contains("Local folder")).Change("folder");
        Save(cut);

        Assert.That(cut.Markup, Does.Contain("Choose a folder first"));
    }

    [Test]
    public void FolderSource_ChoosesOnlyThePickedFolder()
    {
        SeedScanLocations("/music/folder", "/music/other");
        var cut = OpenEditor();
        cut.FindAll("select").First(s => s.TextContent.Contains("Local folder")).Change("folder");
        cut.FindAll("select").First(s => s.TextContent.Contains("/music/folder")).Change("/music/folder");

        // The search-folder checkboxes stay available for playlist jobs;
        // a folder job pins the source folder itself.
        Assert.That(cut.FindAll("details.quick-folders").Count, Is.GreaterThan(0));
        Save(cut);

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.EqualTo(new[] { "/music/folder" }));
    }

    // -- C6: run log with warnings breakdown ------------------------

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
        cut.Find("button.card-menu-trigger").Click();
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
