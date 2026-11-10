using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Rule profiles against a real SQLite database: save and load, live
/// path resolution, and deleting a profile unlinks its jobs while leaving
/// their stored values as the fallback.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class SyncRuleProfileStoreTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SyncRuleProfileStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-rule-profile-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _store = new SyncRuleProfileStore(_factory);
    }

    [TearDown]
    public void TearDown()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Test]
    public async Task SaveAndLoad_RoundTripsEveryField()
    {
        var profile = new SyncRuleProfile
        {
            Name = "Living room",
            Kind = OutputTargetType.PlexApi,
            MediaServerProfileId = "plex-1",
            PlaylistNamePattern = "{job} (Plex)",
            SourceArchiveProfileId = "archive-flac",
            M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{job}.m3u8",
            M3uRelativePaths = true,
            M3uCrlf = true,
            M3uBackslashes = false,
            M3uBom = true,
            PathRemapRules = new() { ["/music"] = "/media/music" },
            ExtensionRemapRules = new() { [".flac"] = ".mp3" },
        };

        await _store.SaveAsync(profile);
        var loaded = await _store.FindAsync(profile.Id);
        var all = await _store.GetAllAsync();

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Name, Is.EqualTo("Living room"));
            Assert.That(loaded.Kind, Is.EqualTo(OutputTargetType.PlexApi));
            Assert.That(loaded.MediaServerProfileId, Is.EqualTo("plex-1"));
            Assert.That(loaded.PlaylistNamePattern, Is.EqualTo("{job} (Plex)"));
            Assert.That(loaded.SourceArchiveProfileId, Is.EqualTo("archive-flac"));
            Assert.That(loaded.M3uOutputFolder, Is.EqualTo("/srv/playlists"));
            Assert.That(loaded.M3uRelativePaths, Is.True);
            Assert.That(loaded.M3uCrlf, Is.True);
            Assert.That(loaded.M3uBom, Is.True);
            Assert.That(loaded.PathRemapRules["/music"], Is.EqualTo("/media/music"));
            Assert.That(loaded.ExtensionRemapRules[".flac"], Is.EqualTo(".mp3"));
            Assert.That(all.Select(p => p.Id), Does.Contain(profile.Id));
        });
    }

    [Test]
    public async Task EnsureDefaults_CreatesOneM3uAndOneProfilePerServer()
    {
        var settings = new SettingsStore(_factory);
        var servers = new MediaServerProfileStore(settings);
        await servers.SaveAsync(new MediaServerProfile
        {
            Id = "jf", Name = "Jellyfin", Kind = "Jellyfin",
            BaseUrl = "http://jf:8096", ApiKey = "key",
        });
        await servers.SaveAsync(new MediaServerProfile
        {
            Id = "px", Name = "Plex", Kind = "Plex",
            BaseUrl = "http://px:32400", ApiKey = "key",
        });

        var first = await _store.EnsureDefaultsAsync(
            "/playlists", await servers.GetAllAsync());
        Assert.Multiple(() =>
        {
            Assert.That(first.Count(p => p.Kind == OutputTargetType.M3uFile),
                Is.EqualTo(1), "one default M3U profile");
            Assert.That(first.Count(p => p.Kind == OutputTargetType.JellyfinApi
                && p.MediaServerProfileId == "jf"), Is.EqualTo(1));
            Assert.That(first.Count(p => p.Kind == OutputTargetType.PlexApi
                && p.MediaServerProfileId == "px"), Is.EqualTo(1));
        });

        var second = await _store.EnsureDefaultsAsync(
            "/playlists", await servers.GetAllAsync());
        Assert.That(second.Count, Is.EqualTo(first.Count),
            "a second call adds nothing");
    }

    [Test]
    public async Task EnsureDefaults_SeedsServerProfilesOnlyOnce_WhenSettingsAreAvailable()
    {
        var settings = new SettingsStore(_factory);
        var store = new SyncRuleProfileStore(_factory, settings);
        var servers = new MediaServerProfileStore(settings);
        await servers.SaveAsync(new MediaServerProfile
        {
            Id = "jf", Name = "Jellyfin", Kind = "Jellyfin",
            BaseUrl = "http://jf:8096", ApiKey = "key",
        });

        var first = await store.EnsureDefaultsAsync(
            "/playlists", await servers.GetAllAsync());
        var jellyfin = first.Single(p => p.MediaServerProfileId == "jf");
        await store.DeleteAsync(jellyfin.Id);

        await servers.SaveAsync(new MediaServerProfile
        {
            Id = "px", Name = "Plex", Kind = "Plex",
            BaseUrl = "http://px:32400", ApiKey = "key",
        });
        var second = await store.EnsureDefaultsAsync(
            "/playlists", await servers.GetAllAsync());

        Assert.Multiple(() =>
        {
            Assert.That(second.Any(p => p.MediaServerProfileId == "jf"), Is.False,
                "a deleted server profile stays deleted");
            Assert.That(second.Any(p => p.MediaServerProfileId == "px"), Is.False,
                "a connection added after seeding does not get a profile automatically");
        });
    }

    [Test]
    public void MapToDomain_PinnedM3uPath_IsNotRecomputedFromTheProfile()
    {
        var profile = new SyncRuleProfile
        {
            Id = "m3u",
            Name = "M3U",
            Kind = OutputTargetType.M3uFile,
            M3uOutputFolder = "/playlists",
            M3uFileNamePattern = "{job}.m3u8",
        };
        var entity = new SyncJobEntity
        {
            Id = "j1",
            Name = "AC/DC",
            TargetsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new
                {
                    kind = "M3uFile",
                    ruleProfileId = "m3u",
                    m3uFileNameOverride = "AC_DC (2).m3u8",
                },
            }),
            OutputTarget = "M3uFile",
        };

        var job = SyncJobMapping.MapToDomain(entity, new[] { profile });

        Assert.That(job.Targets.Single().M3uPath,
            Is.EqualTo("/playlists/AC_DC (2).m3u8"),
            "quick publish keeps its unique path");
    }

    [Test]
    public void ResolveM3uPath_SanitizesTheFileNameOverride()
    {
        var profile = new SyncRuleProfile
        {
            M3uOutputFolder = "/playlists",
            M3uFileNamePattern = "{job}.m3u8",
        };

        var path = SyncRuleProfileStore.ResolveM3uPath(
            profile, "Job", "../../outside.m3u8");
        var noExtension = SyncRuleProfileStore.ResolveM3uPath(
            profile, "Job", "outside");

        Assert.Multiple(() =>
        {
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo("/playlists"));
            Assert.That(Path.GetFileName(path), Is.EqualTo(".._.._outside.m3u8"),
                "the override cannot escape the profile folder");
            Assert.That(Path.GetFileName(noExtension), Is.EqualTo("outside.m3u8"),
                "a missing extension gets the M3U default");
        });
    }

    [Test]
    public void TargetsOf_ExplicitEmptyList_IsNotALegacyTarget()
    {
        var entity = new SyncJobEntity
        {
            Id = "j1",
            Name = "Empty",
            TargetsJson = "[]",
            OutputTarget = "M3uFile",
            M3uOutputPath = "/stale.m3u",
        };

        Assert.That(SyncJobMapping.TargetsOf(entity), Is.Empty);
        var job = SyncJobMapping.MapToDomain(
            entity, Array.Empty<SyncRuleProfile>());
        Assert.Multiple(() =>
        {
            Assert.That(job.Targets, Is.Empty);
            Assert.That(job.HasExplicitTargets, Is.True);
        });
    }

    [Test]
    public async Task Save_MediaServerProfile_DoesNotNeedAnM3uFolder()
    {
        var profile = new SyncRuleProfile
        {
            Id = "jf-profile",
            Name = "Jellyfin",
            Kind = OutputTargetType.JellyfinApi,
            MediaServerProfileId = "jf",
            M3uOutputFolder = "",
        };

        await _store.SaveAsync(profile);

        var loaded = await _store.FindAsync("jf-profile");
        Assert.Multiple(() =>
        {
            Assert.That(loaded!.Kind, Is.EqualTo(OutputTargetType.JellyfinApi));
            Assert.That(loaded.MediaServerProfileId, Is.EqualTo("jf"));
        });
    }

    [Test]
    public void Save_M3uProfile_WithoutAFolder_Throws()
    {
        var profile = new SyncRuleProfile
        {
            Name = "M3U",
            Kind = OutputTargetType.M3uFile,
            M3uOutputFolder = "",
        };

        Assert.That(async () => await _store.SaveAsync(profile),
            Throws.InvalidOperationException.With.Message.Contains("M3U output folder"));
    }

    [Test]
    public void ResolveOutputPath_ExpandsTheJobTokenAndKeepsTheExtension()
    {
        var profile = new SyncRuleProfile
        {
            M3uOutputFolder = "/srv/playlists/",
            M3uFileNamePattern = "{job}",
        };

        var path = SyncRuleProfileStore.ResolveOutputPath(profile, "AC/DC: Live?");

        Assert.That(path, Is.EqualTo(Path.Combine("/srv/playlists", "AC_DC_ Live_.m3u8")));
    }

    [Test]
    public async Task Delete_UnlinksJobs_AndKeepsTheirCustomValues()
    {
        var profile = new SyncRuleProfile { Name = "Old", M3uOutputFolder = "/old" };
        await _store.SaveAsync(profile);
        using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1",
                Name = "Jazz",
                RuleProfileId = profile.Id,
                M3uOutputPath = "/custom/jazz.m3u8",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        Assert.That(await _store.DeleteAsync(profile.Id), Is.True);

        using var check = _factory.CreateDbContext();
        var job = check.SyncJobs.Single(j => j.Id == "j1");
        Assert.Multiple(() =>
        {
            Assert.That(job.RuleProfileId, Is.Null);
            Assert.That(job.M3uOutputPath, Is.EqualTo("/custom/jazz.m3u8"),
                "the job falls back to its own stored path");
            Assert.That(check.SyncRuleProfiles.Any(p => p.Id == profile.Id), Is.False);
        });
    }

    [Test]
    public void MapToDomain_PerTargetProfiles_UseTheirOwnRules()
    {
        var m3u = new SyncRuleProfile
        {
            Id = "m3u",
            Name = "M3U player",
            Kind = OutputTargetType.M3uFile,
            M3uOutputFolder = "/playlists",
            M3uFileNamePattern = "{job}.m3u8",
            PathRemapRules = new() { ["/music"] = "/player/music" },
        };
        var jellyfin = new SyncRuleProfile
        {
            Id = "jf",
            Name = "Jellyfin FLAC",
            Kind = OutputTargetType.JellyfinApi,
            MediaServerProfileId = "jf-1",
            PlaylistNamePattern = "{job} (FLAC)",
            SourceArchiveProfileId = SyncSourceVariants.DefaultArchive,
            PathRemapRules = new() { ["/music"] = "/jellyfin/music" },
        };
        var entity = new SyncJobEntity
        {
            Id = "j1",
            Name = "Jazz",
            TargetsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new { kind = "M3uFile", ruleProfileId = "m3u" },
                new { kind = "JellyfinApi", ruleProfileId = "jf", profileId = "jf-1" },
            }),
            OutputTarget = "M3uFile",
            Schedule = "Manual",
        };

        var job = SyncJobMapping.MapToDomain(entity, new[] { m3u, jellyfin });

        Assert.Multiple(() =>
        {
            var file = job.Targets.Single(t => t.Kind == OutputTargetType.M3uFile);
            Assert.That(file.M3uPath, Is.EqualTo(Path.Combine("/playlists", "Jazz.m3u8")));
            Assert.That(file.PathRemapRules!["/music"], Is.EqualTo("/player/music"));

            var server = job.Targets.Single(t => t.Kind == OutputTargetType.JellyfinApi);
            Assert.That(server.ProfileId, Is.EqualTo("jf-1"));
            Assert.That(server.PlaylistName, Is.EqualTo("Jazz (FLAC)"));
            Assert.That(server.SourceArchiveProfileId,
                Is.EqualTo(SyncSourceVariants.DefaultArchive));
            Assert.That(server.PathRemapRules!["/music"], Is.EqualTo("/jellyfin/music"));
            Assert.That(server.AllowMetadataFallback, Is.False,
                "an archive source must not fall back to the primary item");
        });
    }

    [Test]
    public async Task Delete_RemovesTargetsThatUsedTheProfile()
    {
        var profile = new SyncRuleProfile
        {
            Id = "gone",
            Name = "Gone",
            Kind = OutputTargetType.JellyfinApi,
        };
        await _store.SaveAsync(profile);
        using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1",
                Name = "Jazz",
                TargetsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
                {
                    new { kind = "M3uFile", m3uPath = "/out/jazz.m3u8" },
                    new { kind = "JellyfinApi", ruleProfileId = "gone" },
                }),
                OutputTarget = "M3uFile",
                Schedule = "Manual",
            });
            db.SaveChanges();
        }

        Assert.That(await _store.DeleteAsync(profile.Id), Is.True);

        using var check = _factory.CreateDbContext();
        var job = check.SyncJobs.Single(j => j.Id == "j1");
        var targets = SyncJobMapping.TargetsOf(job);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(1));
            Assert.That(targets[0].Kind, Is.EqualTo(OutputTargetType.M3uFile));
            Assert.That(targets[0].M3uPath, Is.EqualTo("/out/jazz.m3u8"));
        });
    }

    [Test]
    public void ResolvePlaylistName_ExpandsTheJobToken()
    {
        var profile = new SyncRuleProfile { PlaylistNamePattern = "{job} (FLAC)" };
        Assert.That(SyncRuleProfileStore.ResolvePlaylistName(profile, "Jazz"),
            Is.EqualTo("Jazz (FLAC)"));
    }

    [Test]
    public void MapToDomain_ProfileOverridesPathFormatAndRules()
    {
        var profile = new SyncRuleProfile
        {
            Id = "p1",
            Name = "Player",
            M3uOutputFolder = "/player",
            M3uFileNamePattern = "{job}.m3u8",
            M3uRelativePaths = true,
            M3uCrlf = true,
            M3uBom = true,
            M3uBackslashes = true,
            PathRemapRules = new() { ["/music"] = "/media/music" },
            ExtensionRemapRules = new() { [".flac"] = ".mp3" },
        };
        var entity = new SyncJobEntity
        {
            Id = "j1",
            Name = "Jazz",
            M3uOutputPath = "/custom/jazz.m3u8",
            M3uRelativePaths = false,
            M3uCrlf = false,
            M3uBom = false,
            PathRemapRules = "{}",
            ExtensionRemapRules = "{}",
            TargetsJson = null,
            OutputTarget = "M3uFile",
        };

        var job = SyncJobMapping.MapToDomain(entity, profile);

        Assert.Multiple(() =>
        {
            Assert.That(job.RuleProfileId, Is.EqualTo("p1"));
            Assert.That(job.RuleProfileName, Is.EqualTo("Player"));
            Assert.That(job.M3uOutputPath, Is.EqualTo(Path.Combine("/player", "Jazz.m3u8")));
            Assert.That(job.M3uRelativePaths, Is.True);
            Assert.That(job.M3uCrlf, Is.True);
            Assert.That(job.M3uBom, Is.True);
            Assert.That(job.M3uBackslashes, Is.True);
            Assert.That(job.PathRemapRules["/music"], Is.EqualTo("/media/music"));
            Assert.That(job.ExtensionRemapRules[".flac"], Is.EqualTo(".mp3"));
            Assert.That(job.Targets.Single(t => t.Kind == OutputTargetType.M3uFile).M3uPath,
                Is.EqualTo(Path.Combine("/player", "Jazz.m3u8")));
        });
    }
}
