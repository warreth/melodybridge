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
/// The sync job editor, rule profiles and bulk actions: one editor covers
/// a new job, a profile resolves the M3U path live, and a profile or
/// schedule can be applied to several jobs at once.
/// </summary>
[TestFixture]
[Category("UI")]
public class SyncJobsProfilesUiTests
{
    private Bunit.TestContext _ctx = null!;
    private TestSqliteFactory _factory = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-sync-ui-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();

        _ctx.Services.AddDownloadPages(_factory);
        _ctx.Services.AddSingleton<ISyncJobRunner>(new Mock<ISyncJobRunner>().Object);
        _ctx.Services.AddSingleton<IMediaServerDirectory>(new Mock<IMediaServerDirectory>().Object);
        _ctx.Services.AddSingleton<IMediaServerDirectoryFactory>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerDirectoryFactory(
                sp.GetServices<IMediaServerDirectory>()));
        _ctx.Services.AddSingleton<MelodyBridge.Server.Services.NotificationService>();
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginService>(
            new TestServices.StubJellyfinLogin());
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private void SeedPlaylist()
    {
        using var db = _factory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "p1",
            Name = "Jazz Classics",
            SourceUrl = "stub:p1",
            SourcePlatform = Platform.Spotify,
            TrackCount = 4,
        });
        db.ScanLocations.Add(new ScanLocationEntity { Path = "/music" });
        db.SaveChanges();
    }

    private IRenderedComponent<SyncJobs> RenderPage()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Sync jobs")),
            TimeSpan.FromSeconds(3));
        return cut;
    }

    [Test]
    public void NewJob_OpensTheOneEditor()
    {
        SeedPlaylist();
        var cut = RenderPage();

        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".job-quick-modal").Count, Is.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("Create a sync job"));
            Assert.That(cut.Markup, Does.Not.Contain("Step 1 of 5"));
            Assert.That(cut.FindAll(".profile-targets .publish-option").Count,
                Is.GreaterThan(0), "the target list is always visible");
            Assert.That(cut.Markup, Does.Not.Contain("Advanced wizard"),
                "there is no second wizard entry point");
        });
    }

    [Test]
    public async Task NewJob_Editor_SavesWithoutSteps()
    {
        SeedPlaylist();
        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();

        cut.Find("input[placeholder='e.g. My Summer Hits']").Input("Jazz quick");
        cut.FindAll("select").First(s => s.TextContent.Contains("Jazz Classics")).Change("p1");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Create sync job").Click();

        await WaitForJobAsync("Jazz quick");
        using var db = _factory.CreateDbContext();
        var job = db.SyncJobs.Single(j => j.Name == "Jazz quick");
        Assert.That(SyncJobMapping.TargetsOf(job).Single().M3uPath,
            Is.EqualTo("/app/playlists/Jazz quick.m3u8"),
            "the default publish profile owns the path");
    }

    [Test]
    public async Task Edit_OpensAndSavesInOneView()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p1", Name = "Jazz", SourceUrl = "stub:p1", TrackCount = 2,
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1", Name = "Old name", SourceId = "p1", Schedule = "Manual",
                M3uOutputPath = "/out/jazz.m3u8", TargetsJson = null, OutputTarget = "M3uFile",
            });
            db.SaveChanges();
        }

        var cut = RenderPage();
        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".job-quick-modal").Count, Is.EqualTo(1)),
            TimeSpan.FromSeconds(3));
        cut.Find("input[placeholder='e.g. My Summer Hits']").Input("New name");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        await WaitForAssertionAsync(() =>
        {
            using var db = _factory.CreateDbContext();
            return db.SyncJobs.Single(j => j.Id == "j1").Name == "New name";
        });
    }

    [Test]
    public async Task LinkedProfile_ResolvesTheM3uPath()
    {
        SeedPlaylist();
        var profile = new SyncRuleProfile
        {
            Id = "profile-1",
            Name = "Living room",
            M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{job}.m3u8",
        };
        await new SyncRuleProfileStore(_factory).SaveAsync(profile);
        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();

        cut.Find("input[placeholder='e.g. My Summer Hits']").Input("Jazz");
        // The default M3U profile starts ticked; tick the new one instead.
        cut.Find(".profile-targets .publish-option.on input[type='checkbox']").Change(false);
        cut.FindAll("input[type='checkbox']")
            .Single(i => i.GetAttribute("aria-label") == "Send to Living room")
            .Change(true);

        Assert.That(cut.Find(".job-quick-modal").TextContent,
            Does.Contain("/srv/playlists/Jazz.m3u8"));
        Assert.That(cut.Markup, Does.Contain("Living room"));
    }

    [Test]
    public async Task RuleProfilesTab_CreateProfile_Persists()
    {
        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Publish profiles")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("New publish profile")).Click();
        cut.Find("input[placeholder='Living room Jellyfin']").Change("Car");
        cut.Find("input[placeholder='/app/playlists']").Input("/car/playlists");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save profile").Click();

        await WaitForProfileAsync("Car");
        using var db = _factory.CreateDbContext();
        var profile = db.SyncRuleProfiles.Single(p => p.Name == "Car");
        Assert.That(profile.M3uOutputFolder, Is.EqualTo("/car/playlists"));
        Assert.That(cut.Markup, Does.Contain("Car"));
    }

    [Test]
    public async Task ProfileEditor_MediaServerKind_ShowsPlaylistAndSource_AndHidesM3uFolder()
    {
        await new MediaServerProfileStore(new SettingsStore(_factory)).SaveAsync(
            new MediaServerProfile
            {
                Id = "px1", Name = "Home Jellyfin", Kind = "Jellyfin",
                BaseUrl = "http://jf:8096", ApiKey = "key",
            });
        using (var db = _factory.CreateDbContext())
        {
            db.ArchiveProfiles.Add(new ArchiveProfileEntity
            {
                Id = "flac", Name = "FLAC", RootPath = "/archive/flac",
                Format = "auto", Enabled = true,
            });
            db.SaveChanges();
        }

        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Publish profiles")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("New publish profile")).Click();
        cut.Find("input[placeholder='Living room Jellyfin']").Change("Jellyfin FLAC");
        cut.FindAll("select").First(s => s.TextContent.Contains("M3U file")).Change("JellyfinApi");

        Assert.That(cut.FindAll("input[placeholder='/app/playlists']"), Is.Empty,
            "a media server profile does not ask for an M3U folder");
        cut.FindAll("select").First(s => s.TextContent.Contains("Home Jellyfin")).Change("px1");
        cut.FindAll("select").First(s => s.TextContent.Contains("Primary library")).Change("flac");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save profile").Click();

        await WaitForProfileAsync("Jellyfin FLAC");
        using var check = _factory.CreateDbContext();
        var profile = check.SyncRuleProfiles.Single(p => p.Name == "Jellyfin FLAC");
        Assert.Multiple(() =>
        {
            Assert.That(profile.Kind, Is.EqualTo("JellyfinApi"));
            Assert.That(profile.MediaServerProfileId, Is.EqualTo("px1"));
            Assert.That(profile.SourceArchiveProfileId, Is.EqualTo("flac"));
        });
    }

    [Test]
    public async Task ProfileEditor_WrongKindConnection_BlocksSave()
    {
        var servers = new MediaServerProfileStore(new SettingsStore(_factory));
        await servers.SaveAsync(new MediaServerProfile
        {
            Id = "jf", Name = "JF", Kind = "Jellyfin",
            BaseUrl = "http://jf:8096", ApiKey = "key",
        });
        await servers.SaveAsync(new MediaServerProfile
        {
            Id = "px", Name = "PX", Kind = "Plex",
            BaseUrl = "http://px:32400", ApiKey = "key",
        });

        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Publish profiles")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("New publish profile")).Click();
        cut.Find("input[placeholder='Living room Jellyfin']").Change("Wrong kind");
        cut.FindAll("select").First(s => s.TextContent.Contains("M3U file")).Change("JellyfinApi");
        cut.FindAll("select").First(s => s.TextContent.Contains("JF (Jellyfin)")).Change("jf");
        cut.FindAll("select").First(s => s.TextContent.Contains("M3U file")).Change("PlexApi");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save profile").Click();

        Assert.That(cut.Markup, Does.Contain("not Plex"));
        using var db = _factory.CreateDbContext();
        Assert.That(db.SyncRuleProfiles.Any(p => p.Name == "Wrong kind"), Is.False,
            "a connection of the wrong kind is not saved");
    }

    [Test]
    public async Task BulkRemoveProfile_RemovesTheTargetFromSelectedJobs()
    {
        var profile = new SyncRuleProfile
        {
            Id = "profile-remove",
            Name = "Remove me",
            Kind = OutputTargetType.JellyfinApi,
            MediaServerProfileId = "server-1",
            PlaylistNamePattern = "{job}",
        };
        await new SyncRuleProfileStore(_factory).SaveAsync(profile);
        using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.AddRange(
                new SyncJobEntity
                {
                    Id = "j1", Name = "Jazz", Schedule = "Manual",
                    TargetsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
                    {
                        new { kind = "M3uFile", m3uPath = "/old/jazz.m3u8" },
                        new { kind = "JellyfinApi", ruleProfileId = profile.Id, profileId = "server-1" },
                    }),
                    OutputTarget = "M3uFile",
                },
                new SyncJobEntity
                {
                    Id = "j2", Name = "Rock", Schedule = "Manual",
                    TargetsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
                    {
                        new { kind = "M3uFile", m3uPath = "/old/rock.m3u8" },
                        new { kind = "JellyfinApi", ruleProfileId = profile.Id, profileId = "server-1" },
                    }),
                    OutputTarget = "M3uFile",
                });
            db.SaveChanges();
        }

        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Select jobs")).Click();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".row-select").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));
        cut.FindAll(".row-select")[0].Change(true);
        cut.FindAll(".row-select")[1].Change(true);
        cut.Find("select[aria-label='Publish profile to remove']").Change(profile.Id);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Remove profile").Click();

        await WaitForAssertionAsync(() =>
        {
            using var db = _factory.CreateDbContext();
            return db.SyncJobs.ToList().All(job => SyncJobMapping.TargetsOf(job)
                .All(target => target.RuleProfileId != profile.Id));
        });
    }

    [Test]
    public async Task ProfileEdit_UpdatesTheLinkedJobPathLive()
    {
        var profile = new SyncRuleProfile
        {
            Id = "profile-live", Name = "Live", M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{job}.m3u8",
        };
        await new SyncRuleProfileStore(_factory).SaveAsync(profile);
        using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1", Name = "Old", Schedule = "Manual", RuleProfileId = profile.Id,
                M3uOutputPath = "/old/old.m3u8", TargetsJson = null, OutputTarget = "M3uFile",
            });
            db.SaveChanges();
        }

        var cut = RenderPage();
        Assert.That(cut.Markup, Does.Contain("/srv/playlists/Old.m3u8"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Publish profiles")).Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.Find(".profile-editor-modal input[placeholder='/app/playlists']")
            .Input("/new/playlists");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save profile").Click();

        await WaitForAssertionAsync(() =>
        {
            using var db = _factory.CreateDbContext();
            return db.SyncRuleProfiles.Single(p => p.Id == profile.Id).M3uOutputFolder
                == "/new/playlists";
        });
        cut.FindAll("button").First(b => b.TextContent.Contains("Jobs")).Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("/new/playlists/Old.m3u8")),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task BulkApplyProfile_UpdatesSelectedJobs()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Playlists.AddRange(
                new PlaylistEntity { Id = "p1", Name = "One", SourceUrl = "stub:1", TrackCount = 1 },
                new PlaylistEntity { Id = "p2", Name = "Two", SourceUrl = "stub:2", TrackCount = 1 });
            db.SyncJobs.AddRange(
                new SyncJobEntity
                {
                    Id = "j1", Name = "One", SourceId = "p1", Schedule = "Manual",
                    M3uOutputPath = "/old/one.m3u8", TargetsJson = null, OutputTarget = "M3uFile",
                },
                new SyncJobEntity
                {
                    Id = "j2", Name = "Two", SourceId = "p2", Schedule = "Manual",
                    M3uOutputPath = "/old/two.m3u8", TargetsJson = null, OutputTarget = "M3uFile",
                });
            db.SaveChanges();
        }
        var profile = new SyncRuleProfile
        {
            Id = "profile-bulk", Name = "Bulk", M3uOutputFolder = "/bulk",
            M3uFileNamePattern = "{job}.m3u8",
        };
        await new SyncRuleProfileStore(_factory).SaveAsync(profile);

        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Select jobs")).Click();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".row-select").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));
        cut.FindAll(".row-select")[0].Change(true);
        cut.FindAll(".row-select")[1].Change(true);
        cut.Find("select[aria-label='Publish profile to add']").Change(profile.Id);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add profile").Click();

        await WaitForAssertionAsync(() =>
        {
            using var db = _factory.CreateDbContext();
            return db.SyncJobs.Count(j => j.RuleProfileId == "profile-bulk") == 2
                && db.SyncJobs.ToList().All(job => SyncJobMapping.TargetsOf(job)
                    .Any(target => target.RuleProfileId == "profile-bulk"));
        });
    }

    private async Task WaitForJobAsync(string name)
    {
        for (var i = 0; i < 40; i++)
        {
            using var db = _factory.CreateDbContext();
            if (db.SyncJobs.Any(j => j.Name == name)) return;
            await Task.Delay(100);
        }
        Assert.Fail($"job '{name}' was not saved");
    }

    private async Task WaitForProfileAsync(string name)
    {
        for (var i = 0; i < 40; i++)
        {
            using var db = _factory.CreateDbContext();
            if (db.SyncRuleProfiles.Any(p => p.Name == name)) return;
            await Task.Delay(100);
        }
        Assert.Fail($"profile '{name}' was not saved");
    }

    private static async Task WaitForAssertionAsync(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("condition was not met");
    }
}
