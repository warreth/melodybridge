using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The quick publish path: one Manual job per selected playlist, named
/// after it, pointing at the ticked media server profiles. A playlist
/// that already has a job is never given a second one.
/// </summary>
[TestFixture]
public class QuickSyncJobsTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-quick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "quick.db"));
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
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

    private static MediaServerProfile Profile(string id, string name, string kind) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        BaseUrl = "http://server:8096",
        ApiKey = "key",
    };

    [Test]
    public async Task Create_MakesOneNamedJobPerPlaylist_WithEveryTickedProfile()
    {
        await SeedPlaylistsAsync(("p1", "Jazz Classics"), ("p2", "Synthwave"));

        var created = await QuickSyncJobs.CreateAsync(_factory,
            new[] { ("p1", "Jazz Classics"), ("p2", "Synthwave") },
            new[]
            {
                Profile("px1", "Home Jellyfin", "Jellyfin"),
                Profile("px2", "Living room Plex", "Plex"),
            });

        Assert.That(created, Has.Count.EqualTo(2));
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
        await using (var db = _factory.CreateDbContext())
        {
            db.SyncJobs.Add(new SyncJobEntity { Name = "Taken", SourceId = "p1" });
            await db.SaveChangesAsync();
        }

        var created = await QuickSyncJobs.CreateAsync(_factory,
            new[] { ("p1", "Taken"), ("p2", "Free") },
            new[] { Profile("px1", "JF", "Jellyfin") });

        Assert.That(created, Has.Count.EqualTo(1));
        Assert.That(created[0].Name, Is.EqualTo("Free"));
        await using var check = _factory.CreateDbContext();
        Assert.That(check.SyncJobs.Count(), Is.EqualTo(2),
            "no duplicate job for the playlist that already had one");
    }

    [Test]
    public async Task Create_WithoutProfiles_DoesNothing()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));

        var created = await QuickSyncJobs.CreateAsync(_factory, new[] { ("p1", "Jazz") },
            Array.Empty<MediaServerProfile>());

        Assert.That(created, Is.Empty);
        await using var db = _factory.CreateDbContext();
        Assert.That(db.SyncJobs.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task Create_SubsonicAliasProfiles_MapToTheSubsonicTarget()
    {
        await SeedPlaylistsAsync(("p1", "Jazz"));

        await QuickSyncJobs.CreateAsync(_factory, new[] { ("p1", "Jazz") },
            new[] { Profile("px1", "Navidrome", "Navidrome") });

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
