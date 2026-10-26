using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The quick publish path: one Manual job per selected playlist, named
/// after it, pointing at the ticked media server profiles. A playlist
/// that already has a job is never given a second one, and playlists or
/// profiles that vanished after the dialog opened are reported instead of
/// stored as dangling targets.
/// </summary>
[TestFixture]
public class QuickSyncJobsTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private MediaServerProfileStore _profiles = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-quick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "quick.db"));
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        _profiles = new MediaServerProfileStore(new SettingsStore(_factory));
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task SeedPlaylistsAsync(params (string Id, string Name)[] playlists)
    {
        await using var db = _factory.CreateDbContext();
        foreach (var (id, name) in playlists)
        {
            db.Playlists.Add(new PlaylistEntity { Id = id, Name = name, SourceUrl = $"stub:{id}" });
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedProfileAsync(string id, string name, string kind)
    {
        await _profiles.SaveAsync(new MediaServerProfile
        {
            Id = id,
            Name = name,
            Kind = kind,
            BaseUrl = "http://server:8096",
            ApiKey = "key",
        });
    }

    private async Task SeedRuleProfileAsync(SyncRuleProfile profile)
        => await new SyncRuleProfileStore(_factory).SaveAsync(profile);

    [Test]
    public async Task Create_MakesOneNamedJobPerPlaylist_WithEveryTickedProfile()
    {
        await SeedPlaylistsAsync(("p1", "Jazz Classics"), ("p2", "Synthwave"));
        await SeedProfileAsync("px1", "Home Jellyfin", "Jellyfin");
        await SeedProfileAsync("px2", "Living room Plex", "Plex");

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz Classics"), ("p2", "Synthwave") },
            new[] { "px1", "px2" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Has.Count.EqualTo(2));
            Assert.That(result.Skipped, Is.Empty);
        });
        await using var db = _factory.CreateDbContext();
        var stored = db.SyncJobs.OrderBy(j => j.Name).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(stored.Select(j => j.Name), Is.EqualTo(new[] { "Jazz Classics", "Synthwave" }));
            Assert.That(stored.Select(j => j.SourceId), Is.EqualTo(new[] { "p1", "p2" }));
            Assert.That(stored.All(j => j.Schedule == "Manual"), Is.True,
                "the quick path never guesses a schedule");
            Assert.That(stored.All(j => j.CreatedAt != null), Is.True,
                "the scheduler anchor is set like the wizard sets it");
        });

        var targets = SyncJobMapping.TargetsOf(stored[0]);
        Assert.That(targets.Select(t => t.Kind), Is.EqualTo(new[]
        {
            OutputTargetType.JellyfinApi, OutputTargetType.PlexApi,
        }));
        Assert.That(targets.Select(t => t.ProfileId), Is.EqualTo(new[] { "px1", "px2" }));
    }

    [Test]
    public async Task Create_SkipsPlaylistsThatAlreadyHaveAJob()
    {
        await SeedPlaylistsAsync(("p1", "Taken"), ("p2", "Free"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");
        await using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity { Name = "Taken", SourceId = "p1" });
            await db.SaveChangesAsync();
        }

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Taken"), ("p2", "Free") }, new[] { "px1" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Has.Count.EqualTo(1));
            Assert.That(result.Created[0].Name, Is.EqualTo("Free"));
            Assert.That(result.Skipped.Single().Playlist, Is.EqualTo("Taken"));
            Assert.That(result.Skipped.Single().Reason, Does.Contain("already has a sync job"));
        });
        await using var check = _factory.CreateDbContext();
        Assert.That(check.SyncJobs.Count(), Is.EqualTo(2),
            "no duplicate job for the playlist that already had one");
    }

    [Test]
    public async Task Create_SkipsAPlaylistThatWasDeletedAfterTheDialogOpened()
    {
        await SeedProfileAsync("px1", "JF", "Jellyfin");

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("gone", "Deleted") }, new[] { "px1" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Skipped.Single().Reason, Does.Contain("no longer exists"));
        });
        await using var db = _factory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task Create_SkipsAProfileThatWasDeletedAfterTheDialogOpened()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");
        await _profiles.DeleteAsync("px1");

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, new[] { "px1" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Skipped.Single().Reason, Does.Contain("profile no longer exists"));
        });
        await using var db = _factory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task Create_WithoutProfiles_DoesNothing()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Skipped.Single().Reason, Does.Contain("no target"));
        });
        await using var db = _factory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task Create_WithSchedule_StoresThePresetForEveryJob()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"), ("p2", "Synthwave"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz"), ("p2", "Synthwave") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.FromCron("0 3 * * 1")));

        Assert.That(result.Created, Has.Count.EqualTo(2));
        await using var db = _factory.CreateDbContext();
        var stored = db.SyncJobs.ToList();
        Assert.That(stored.Select(j => j.Schedule), Is.All.EqualTo("Weekly"),
            "a named preset stores its label so the scheduler resolves the shared cron");
        Assert.That(stored.Select(j => j.CronExpression), Is.All.Null,
            "the preset does not need a copied cron string");
    }

    [Test]
    public async Task Create_WithM3u_AddsAPerPlaylistM3uTarget()
    {
        await SeedPlaylistsAsync(("p1", "Jazz Classics"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz Classics") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true, M3uDirectory: "/playlists"));

        Assert.That(result.Created, Has.Count.EqualTo(1));
        await using var db = _factory.CreateDbContext();
        var job = db.SyncJobs.Single();
        var targets = SyncJobMapping.TargetsOf(job);
        Assert.Multiple(() =>
        {
            Assert.That(targets.Select(t => t.Kind),
                Is.EqualTo(new[] { OutputTargetType.M3uFile, OutputTargetType.JellyfinApi }));
            Assert.That(targets[0].M3uPath, Is.EqualTo(Path.Combine("/playlists", "Jazz Classics.m3u8")));
            Assert.That(job.M3uCrlf, Is.True,
                "quick publish uses the widest text format it cannot configure");
            Assert.That(job.M3uBom, Is.True);
        });
    }

    [Test]
    public async Task Create_WithM3u_KeepsCollidingPlaylistNamesInSeparateFiles()
    {
        await SeedPlaylistsAsync(("p1", "AC/DC"), ("p2", "AC_DC"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "AC/DC"), ("p2", "AC_DC") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true, M3uDirectory: "/playlists"));

        Assert.That(result.Created, Has.Count.EqualTo(2));
        await using var db = _factory.CreateDbContext();
        var paths = db.SyncJobs
            .AsEnumerable()
            .Select(j => SyncJobMapping.TargetsOf(j)
                .Single(t => t.Kind == OutputTargetType.M3uFile).M3uPath)
            .ToList();
        Assert.That(paths, Is.EquivalentTo(new[]
        {
            Path.Combine("/playlists", "AC_DC.m3u8"),
            Path.Combine("/playlists", "AC_DC (2).m3u8"),
        }), "names that sanitize to the same file get distinct paths");
        Assert.That(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task Create_WithM3u_AvoidsAPathAnExistingJobAlreadyUses()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");
        await using (var db = _factory.CreateDbContext())
        {
            var existing = new SyncJobEntity { Name = "Old Jazz", SourceId = "other" };
            SyncJobMapping.ApplyTargets(existing,
                new[] { new SyncTarget(OutputTargetType.M3uFile, M3uPath: "/playlists/Jazz.m3u8") });
            db.SyncJobs.Add(existing);
            await db.SaveChangesAsync();
        }

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true, M3uDirectory: "/playlists"));

        Assert.That(result.Created, Has.Count.EqualTo(1));
        var created = result.Created[0];
        Assert.That(SyncJobMapping.TargetsOf(created)
                .Single(t => t.Kind == OutputTargetType.M3uFile).M3uPath,
            Is.EqualTo(Path.Combine("/playlists", "Jazz (2).m3u8")),
            "the new job never overwrites a file another job already owns");
    }

    [Test]
    public async Task Create_WithM3uOnly_WorksWithoutAProfile()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, Array.Empty<string>(),
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true, M3uDirectory: "/playlists"));

        Assert.That(result.Created, Has.Count.EqualTo(1));
        await using var db = _factory.CreateDbContext();
        var job = db.SyncJobs.Single();
        Assert.That(SyncJobMapping.TargetsOf(job).Single().Kind,
            Is.EqualTo(OutputTargetType.M3uFile));
    }

    [Test]
    public async Task Create_WithRuleProfile_LinksTheJobAndUsesItsRules()
    {
        await SeedPlaylistsAsync(("p1", "Jazz Classics"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");
        await SeedRuleProfileAsync(new SyncRuleProfile
        {
            Id = "rules-1",
            Name = "Living room",
            M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{playlist}-tv.m3u8",
            M3uCrlf = false,
            M3uBom = false,
            M3uRelativePaths = true,
            PathRemapRules = new Dictionary<string, string> { ["/music"] = "/media/music" },
            ExtensionRemapRules = new Dictionary<string, string> { [".flac"] = ".mp3" },
        });
        var rulesStore = new SyncRuleProfileStore(_factory);

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz Classics") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true,
                M3uDirectory: "/ignored", RuleProfileId: "rules-1"),
            rulesStore);

        Assert.That(result.Created, Has.Count.EqualTo(1));
        await using var db = _factory.CreateDbContext();
        var job = db.SyncJobs.Single();
        var file = SyncJobMapping.TargetsOf(job).Single(t => t.Kind == OutputTargetType.M3uFile);
        Assert.Multiple(() =>
        {
            Assert.That(job.RuleProfileId, Is.EqualTo("rules-1"),
                "the quick job follows the profile on every run");
            Assert.That(file.M3uPath, Is.EqualTo(
                Path.Combine("/srv/playlists", "Jazz Classics-tv.m3u8")),
                "the profile's folder and file name pattern win over the custom folder");
            Assert.That(job.M3uCrlf, Is.False, "the profile's text format is stored as a fallback");
            Assert.That(job.M3uBom, Is.False);
            Assert.That(job.M3uRelativePaths, Is.True);
            Assert.That(job.PathRemapRules, Does.Contain("/media/music"));
            Assert.That(job.ExtensionRemapRules, Does.Contain(".mp3"));
        });
    }

    [Test]
    public async Task Create_WithDeletedRuleProfile_SkipsEveryPlaylist()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");
        var rulesStore = new SyncRuleProfileStore(_factory);

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true, RuleProfileId: "gone"),
            rulesStore);

        Assert.Multiple(() =>
        {
            Assert.That(result.Created, Is.Empty);
            Assert.That(result.Skipped.Single().Reason,
                Does.Contain("rule profile no longer exists"));
        });
        await using var db = _factory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.Zero);
    }

    [Test]
    public async Task Create_WithRuleProfile_KeepsCollidingNamesInSeparateFiles()
    {
        await SeedPlaylistsAsync(("p1", "AC/DC"), ("p2", "AC_DC"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");
        await SeedRuleProfileAsync(new SyncRuleProfile
        {
            Id = "rules-1",
            Name = "Living room",
            M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{job}.m3u8",
        });
        var rulesStore = new SyncRuleProfileStore(_factory);

        var result = await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "AC/DC"), ("p2", "AC_DC") }, new[] { "px1" },
            new QuickSyncOptions(ScanSchedule.Manual, M3uEnabled: true, RuleProfileId: "rules-1"),
            rulesStore);

        var paths = result.Created
            .Select(j => SyncJobMapping.TargetsOf(j)
                .Single(t => t.Kind == OutputTargetType.M3uFile).M3uPath)
            .ToList();
        Assert.That(paths, Is.EquivalentTo(new[]
        {
            Path.Combine("/srv/playlists", "AC_DC.m3u8"),
            Path.Combine("/srv/playlists", "AC_DC (2).m3u8"),
        }));
    }

    [Test]
    public async Task Create_TwoConcurrentCalls_MakeOneJobPerPlaylist()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));
        await SeedProfileAsync("px1", "JF", "Jellyfin");

        var first = QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, new[] { "px1" });
        var second = QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, new[] { "px1" });
        var results = await Task.WhenAll(first, second);

        Assert.That(results.Sum(r => r.Created.Count), Is.EqualTo(1),
            "two tabs publishing the same playlist create one job");
        Assert.That(results.SelectMany(r => r.Skipped).Single().Reason,
            Does.Contain("already has a sync job"));
        await using var db = _factory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Create_SubsonicAliasProfiles_MapToTheSubsonicTarget()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));
        await SeedProfileAsync("px1", "Navidrome", "Navidrome");

        await QuickSyncJobs.CreateAsync(_factory, _profiles,
            new[] { ("p1", "Jazz") }, new[] { "px1" });

        await using var db = _factory.CreateDbContext();
        var job = db.SyncJobs.Single();
        Assert.That(SyncJobMapping.TargetsOf(job).Single().Kind,
            Is.EqualTo(OutputTargetType.SubsonicApi));
    }

    [Test]
    public async Task PlaylistsWithJob_ReturnsEveryBoundSource()
    {
        await SeedPlaylistsAsync(("p1", "A"), ("p2", "B"), ("p3", "C"));
        await using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity { Name = "A", SourceId = "p1" });
            db.SyncJobs.Add(new SyncJobEntity { Name = "B", SourceId = "p3" });
            db.SyncJobs.Add(new SyncJobEntity { Name = "Folder job", SourceId = null });
            await db.SaveChangesAsync();
        }

        var ids = await QuickSyncJobs.PlaylistsWithJobAsync(_factory);

        Assert.That(ids, Is.EquivalentTo(new[] { "p1", "p3" }));
    }
}
