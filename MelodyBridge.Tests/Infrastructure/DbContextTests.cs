using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class DbContextTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"DbContextTest_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Test]
    public async Task TrackEntity_CanStoreAndRetrieve()
    {
        using var db = CreateDbContext();
        var entity = new TrackEntity
        {
            MelodyId = "MELODY-001",
            Title = "Test Song",
            Artist = "Test Artist",
            MediaType = ".flac",
            CurrentPath = "/music/test.flac"
        };
        db.Tracks.Add(entity);
        await db.SaveChangesAsync();

        var loaded = await db.Tracks.FindAsync(entity.Id);
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.MelodyId, Is.EqualTo("MELODY-001"));
            Assert.That(loaded.Title, Is.EqualTo("Test Song"));
            Assert.That(loaded.Artist, Is.EqualTo("Test Artist"));
            Assert.That(loaded.MediaType, Is.EqualTo(".flac"));
            Assert.That(loaded.CurrentPath, Is.EqualTo("/music/test.flac"));
        });
    }

    [Test]
    public async Task TrackEntity_CanStoreDuplicateMelodyId_InMemory()
    {
        // Note: EF Core InMemory provider does not enforce unique indexes.
        // This test verifies InMemory behavior (allows duplicates).
        using var db = CreateDbContext();
        db.Tracks.Add(new TrackEntity { MelodyId = "dup-id", Title = "First" });
        await db.SaveChangesAsync();

        db.Tracks.Add(new TrackEntity { MelodyId = "dup-id", Title = "Second" });
        await db.SaveChangesAsync(); // InMemory allows duplicates — no exception

        var count = await db.Tracks.CountAsync(t => t.MelodyId == "dup-id");
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task TrackEntity_UpdatePath()
    {
        using var db = CreateDbContext();
        var entity = new TrackEntity
        {
            MelodyId = "MELODY-002",
            Title = "Move Test",
            CurrentPath = "/old/path/song.flac"
        };
        db.Tracks.Add(entity);
        await db.SaveChangesAsync();

        entity.CurrentPath = "/new/path/song.flac";
        db.Tracks.Update(entity);
        await db.SaveChangesAsync();

        var loaded = await db.Tracks.FindAsync(entity.Id);
        Assert.That(loaded!.CurrentPath, Is.EqualTo("/new/path/song.flac"));
    }

    [Test]
    public async Task PlaylistEntity_CanStoreAndRetrieve()
    {
        using var db = CreateDbContext();
        var entity = new PlaylistEntity
        {
            Name = "My Playlist",
            SourceIdentifier = "spotify:playlist:abc123"
        };
        db.Playlists.Add(entity);
        await db.SaveChangesAsync();

        var loaded = await db.Playlists.FindAsync(entity.Id);
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Name, Is.EqualTo("My Playlist"));
            Assert.That(loaded.SourceIdentifier, Is.EqualTo("spotify:playlist:abc123"));
        });
    }

    [Test]
    public async Task ProviderStateRow_CanStoreAndRetrieve()
    {
        using var db = CreateDbContext();
        var entity = new ProviderStateRow
        {
            ProviderId = "test-provider",
            IsEnabled = false
        };
        db.ProviderStates.Add(entity);
        await db.SaveChangesAsync();

        var loaded = await db.ProviderStates.FindAsync("test-provider");
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.IsEnabled, Is.False);
        });
    }

    [Test]
    public async Task ProviderStateRow_DefaultIsEnabled()
    {
        using var db = CreateDbContext();
        var entity = new ProviderStateRow
        {
            ProviderId = "new-provider",
            // IsEnabled defaults to true
        };
        db.ProviderStates.Add(entity);
        await db.SaveChangesAsync();

        var loaded = await db.ProviderStates.FindAsync("new-provider");
        Assert.That(loaded!.IsEnabled, Is.True);
    }

    [Test]
    public async Task TrackEntity_NullableFields_CanBeNull()
    {
        using var db = CreateDbContext();
        var entity = new TrackEntity
        {
            MelodyId = "null-test",
            // Title, Artist, MediaType, CurrentPath all null
        };
        db.Tracks.Add(entity);
        await db.SaveChangesAsync();

        var loaded = await db.Tracks.FindAsync(entity.Id);
        Assert.Multiple(() =>
        {
            Assert.That(loaded!.MelodyId, Is.EqualTo("null-test"));
            Assert.That(loaded.Title, Is.Null);
            Assert.That(loaded.Artist, Is.Null);
            Assert.That(loaded.MediaType, Is.Null);
            Assert.That(loaded.CurrentPath, Is.Null);
        });
    }

    [Test]
    public async Task MultipleTrackEntities_CanBeQueried()
    {
        using var db = CreateDbContext();
        for (int i = 1; i <= 5; i++)
        {
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = $"MELODY-{i:D3}",
                Title = $"Song {i}",
                Artist = "Various Artists"
            });
        }
        await db.SaveChangesAsync();

        var count = await db.Tracks.CountAsync();
        Assert.That(count, Is.EqualTo(5));

        var songs = await db.Tracks.Where(t => t.Artist == "Various Artists").ToListAsync();
        Assert.That(songs, Has.Count.EqualTo(5));
    }
}
