using TestContext = Bunit.TestContext;
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
/// Source naming, the easy M3U compatibility presets and the editing
/// header in the single sync job editor. A playlist source must show its
/// name and platform ("My Summer Hits (Spotify)"), never the raw GUID,
/// and a job from before the presets keeps its plain text format.
/// </summary>
[TestFixture]
[Category("UI")]
public class SyncJobsReviewUiTests
{
    private TestContext _ctx = null!;
    private InMemFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsReview_{Guid.NewGuid()}")
            .Options;
        _dbFactory = new InMemFactory(options);
        _ctx.Services.AddSingleton<ISyncJobRunner>(new Mock<ISyncJobRunner>().Object);
        _ctx.Services.AddSingleton<IMediaServerDirectory>(new Mock<IMediaServerDirectory>().Object);
        // Kind-based lookup through the factory, like the real app.
        _ctx.Services.AddSingleton<IMediaServerDirectoryFactory>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerDirectoryFactory(
                sp.GetServices<IMediaServerDirectory>()));
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_dbFactory);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.SettingsStore(_dbFactory));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.SyncRuleProfileStore(_dbFactory));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.ArchiveProfileStore(_dbFactory));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.FolderBrowser(_dbFactory));
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

    private void SeedPlaylist(string id, string name, Platform platform)
    {
        using var db = _dbFactory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = id,
            Name = name,
            SourcePlatform = platform,
            SourceUrl = "stub:" + id,
        });
        db.SaveChanges();
    }

    private IRenderedComponent<SyncJobs> OpenEditorWithPlaylistSource()
    {
        SeedPlaylist("p1", "My Summer Hits", Platform.Spotify);
        SeedScanLocations("/music/a");
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        cut.FindAll("select")
            .Single(s => s.QuerySelectorAll("option")
                .Any(o => o.GetAttribute("value") == "p1"))
            .Change("p1");
        return cut;
    }

    [Test]
    public void Editor_PlaylistSource_ShowsNameAndPlatform_NotRawGuid()
    {
        var cut = OpenEditorWithPlaylistSource();
        var option = cut.FindAll("option").Single(o => o.GetAttribute("value") == "p1");
        Assert.That(option.TextContent, Does.Contain("My Summer Hits (Spotify)"),
            "the source select shows name + platform");
        Assert.That(option.TextContent.Trim(), Is.Not.EqualTo("p1"),
            "the raw source id is not shown to the user");
    }

    [Test]
    public void Editor_PlaylistSource_EmptySearchMeansEveryFolder()
    {
        var cut = OpenEditorWithPlaylistSource();
        Assert.That(cut.Find("details.quick-folders").TextContent,
            Does.Contain("Search folders (all)"));
        Assert.That(cut.Markup, Does.Contain("Leave all unticked to search every folder"));
    }

    [Test]
    public void Editor_FolderSource_ShowsAndSavesTheFolderPath()
    {
        SeedScanLocations("/music/folder");
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        cut.FindAll("select").First(s => s.TextContent.Contains("Local folder")).Change("folder");
        cut.FindAll("select").First(s => s.TextContent.Contains("/music/folder"))
            .Change("/music/folder");

        Assert.That(cut.Markup, Does.Contain("/music/folder"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        using var db = _dbFactory.CreateDbContext();
        var job = db.SyncJobs.AsNoTracking().Single();
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(job.SearchLocationPaths);
        Assert.That(paths, Is.EqualTo(new[] { "/music/folder" }));
    }

    [Test]
    public void NewJob_DefaultsToWidestCompatibility_AndAPrefilledM3u8Path()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.Find("input[placeholder='e.g. My Summer Hits']").Input("My Summer Hits");

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find(".job-quick-modal").TextContent,
                Does.Contain("My Summer Hits.m3u8"),
                "the ticked M3U profile shows the file it writes");
            Assert.That(cut.Markup, Does.Not.Contain("Windows line endings (CRLF)"),
                "the profile owns the text format; the job editor does not repeat it");
        });

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        cut.WaitForAssertion(() =>
        {
            using var db = _dbFactory.CreateDbContext();
            var job = db.SyncJobs.Single();
            Assert.Multiple(() =>
            {
                Assert.That(job.M3uCrlf, Is.True);
                Assert.That(job.M3uBom, Is.True);
                Assert.That(job.M3uBackslashes, Is.False);
                Assert.That(SyncJobMapping.TargetsOf(job).Single().M3uPath,
                    Does.EndWith(".m3u8"));
            });
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void CarPreset_IsSavedOnThePublishProfile()
    {
        var cut = OpenEditorWithPlaylistSource();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "New publish profile").Click();
        cut.Find("input[placeholder='Living room Jellyfin']").Change("Car profile");
        cut.FindAll("select")
            .Single(s => s.TextContent.Contains("Widest compatibility"))
            .Change("car");
        Assert.That(cut.Markup, Does.Contain("backslashes"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save profile").Click();

        cut.WaitForAssertion(() =>
        {
            using var db = _dbFactory.CreateDbContext();
            var profile = db.SyncRuleProfiles.Single(p => p.Name == "Car profile");
            Assert.Multiple(() =>
            {
                Assert.That(profile.M3uCrlf, Is.True);
                Assert.That(profile.M3uBom, Is.True);
                Assert.That(profile.M3uBackslashes, Is.True);
            });
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void EditingLegacyJob_ShowsTheLegacyTargetAndAsksForAProfile()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p1",
                Name = "Legacy",
                SourcePlatform = Platform.Spotify,
                SourceUrl = "stub:p1",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-old",
                Name = "Old Playlist",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out/old.m3u",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("From an older version"));
            Assert.That(cut.Markup, Does.Contain("(legacy)"));
            Assert.That(cut.Markup, Does.Contain("Convert to a profile"));
        });
    }

    [Test]
    public void EditingLegacyJob_KeepsItsPathAndExtensionRules()
    {
        var paths = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["/music"] = "/media/music" });
        var exts = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { [".flac"] = ".mp3" });
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p1",
                Name = "Legacy rules",
                SourceUrl = "stub:p1",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-legacy-rules",
                Name = "Legacy rules",
                SourceId = "p1",
                SearchLocationPaths = "[]",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out/old.m3u",
                PathRemapRules = paths,
                ExtensionRemapRules = exts,
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        using var db2 = _dbFactory.CreateDbContext();
        var saved = db2.SyncJobs.AsNoTracking().Single(j => j.Id == "job-legacy-rules");
        Assert.Multiple(() =>
        {
            Assert.That(saved.PathRemapRules, Does.Contain("/media/music"),
                "a surviving legacy target keeps the job's path rules");
            Assert.That(saved.ExtensionRemapRules, Does.Contain(".mp3"),
                "and its extension rules");
        });
    }

    [Test]
    public void EditingHeader_ShowsJobName_AndEditEyebrow()
    {
        using (var db = _dbFactory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "job-e",
                Name = "Weekly Sync",
                SourceId = "p-none",
                SearchLocationPaths = "[]",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/out/x.m3u",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<SyncJobs>();
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        Assert.That(cut.Find(".wizard-header h2").TextContent.Trim(), Is.EqualTo("Weekly Sync"));
        Assert.That(cut.Find(".wizard-header .eyebrow").TextContent, Does.Contain("Edit sync job"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
