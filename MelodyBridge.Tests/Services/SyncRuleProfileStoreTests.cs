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
            TargetsJson = "[]",
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
