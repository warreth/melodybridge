using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The library move behind a Music path or Archive path change, against a
/// real SQLite database and real files on disk: files land at the same
/// relative spot under the new root, playlists and scan locations follow,
/// shared paths move once, and nothing is ever overwritten.
/// </summary>
[TestFixture]
public class LibraryPathMoverTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "move.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private LibraryPathMover NewMover() => new(_factory, NullLogger<LibraryPathMover>.Instance);

    /// <summary>A real file under the root; returns its path.</summary>
    private string WriteFile(string relative, string content = "audio bytes")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task MoveAsync_MusicPath_MovesFilesPlaylistsAndScanLocations()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var file = WriteFile("music-old/My Mix/song.mp3");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "My Mix",
                SourceUrl = "stub:pl",
                TargetDirectory = oldRoot,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                        DownloadStatus = "downloaded", CurrentPath = file,
                    },
                },
            });
            db.ScanLocations.Add(new ScanLocationEntity { Path = oldRoot });
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(oldRoot, newRoot, null, null);

        var expected = Path.Combine(newRoot, "My Mix", "song.mp3");
        Assert.That(result.Files, Is.EqualTo(1));
        Assert.That(result.Playlists, Is.EqualTo(1));
        Assert.That(result.ScanLocations, Is.EqualTo(1));
        Assert.That(File.Exists(file), Is.False, "the old name is gone");
        Assert.That(File.Exists(expected), Is.True, "the file lands at the same relative spot");
        Assert.That(File.ReadAllText(expected), Is.EqualTo("audio bytes"));

        await using var check = await _factory.CreateDbContextAsync();
        Assert.That((await check.Tracks.AsNoTracking().SingleAsync()).CurrentPath, Is.EqualTo(expected));
        Assert.That((await check.Playlists.AsNoTracking().SingleAsync()).TargetDirectory, Is.EqualTo(newRoot));
        Assert.That((await check.ScanLocations.AsNoTracking().SingleAsync()).Path, Is.EqualTo(newRoot));
    }

    [Test]
    public async Task MoveAsync_ArchivePath_MovesArchiveCopies()
    {
        var oldArchive = Path.Combine(_root, "archive-old");
        var newArchive = Path.Combine(_root, "archive-new");
        var primary = WriteFile("music/My Mix/song.mp3");
        var archive = WriteFile("archive-old/My Mix/song.flac", "archive bytes");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "My Mix",
                SourceUrl = "stub:pl",
                TargetDirectory = Path.Combine(_root, "music"),
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                        DownloadStatus = "downloaded", CurrentPath = primary, ArchivePath = archive,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(null, null, oldArchive, newArchive);

        var expected = Path.Combine(newArchive, "My Mix", "song.flac");
        Assert.That(result.Files, Is.EqualTo(1));
        Assert.That(File.Exists(expected), Is.True);
        Assert.That(File.ReadAllText(expected), Is.EqualTo("archive bytes"));
        Assert.That(File.Exists(archive), Is.False);
        Assert.That(File.Exists(primary), Is.True, "the music root was not part of this change");

        await using var check = await _factory.CreateDbContextAsync();
        Assert.That((await check.Tracks.AsNoTracking().SingleAsync()).ArchivePath, Is.EqualTo(expected));
    }

    [Test]
    public async Task MoveAsync_SharedPath_MovesOnceAndEveryRowFollows()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var shared = WriteFile("music-old/song.mp3");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var id in new[] { "pl-a", "pl-b" })
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = id,
                    Name = id,
                    SourceUrl = "stub:" + id,
                    TargetDirectory = oldRoot,
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                            DownloadStatus = "downloaded", CurrentPath = shared,
                        },
                    },
                });
            }
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(oldRoot, newRoot, null, null);

        var expected = Path.Combine(newRoot, "song.mp3");
        Assert.That(result.Files, Is.EqualTo(1), "one inode moves once");
        Assert.That(result.FailedFiles, Is.EqualTo(0), "the second row follows the moved file");
        await using var check = await _factory.CreateDbContextAsync();
        Assert.That(await check.Tracks.AsNoTracking().Select(t => t.CurrentPath).Distinct().ToListAsync(),
            Is.EqualTo(new[] { expected }));
    }

    [Test]
    public async Task MoveAsync_OccupiedDestination_KeepsTheFileAndRow()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var file = WriteFile("music-old/song.mp3", "old bytes");
        var occupied = WriteFile("music-new/song.mp3", "other bytes");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "My Mix",
                SourceUrl = "stub:pl",
                TargetDirectory = oldRoot,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                        DownloadStatus = "downloaded", CurrentPath = file,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(oldRoot, newRoot, null, null);

        Assert.That(result.Files, Is.EqualTo(0));
        Assert.That(result.FailedFiles, Is.EqualTo(1), "the clash is counted, not hidden");
        Assert.That(File.ReadAllText(file), Is.EqualTo("old bytes"), "the source stays where it is");
        Assert.That(File.ReadAllText(occupied), Is.EqualTo("other bytes"), "nothing is overwritten");
        await using var check = await _factory.CreateDbContextAsync();
        Assert.That((await check.Tracks.AsNoTracking().SingleAsync()).CurrentPath, Is.EqualTo(file));
        Assert.That((await check.Playlists.AsNoTracking().SingleAsync()).TargetDirectory,
            Is.EqualTo(newRoot), "the playlist still follows: new downloads land in the new root");
    }

    [Test]
    public async Task MoveAsync_NestedRoots_AreRefused()
    {
        var oldRoot = Path.Combine(_root, "music");
        var nested = Path.Combine(oldRoot, "inner");
        var file = WriteFile("music/song.mp3");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                DownloadStatus = "downloaded", CurrentPath = file,
            });
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(oldRoot, nested, null, null);

        Assert.That(result.Files, Is.EqualTo(0), "moving a root inside itself is ambiguous and refused");
        Assert.That(File.Exists(file), Is.True);
    }

    [Test]
    public async Task MoveAsync_ExplicitFoldersUnderTheOldRoot_AreRebased()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var explicitFolder = Path.Combine(oldRoot, "rock");
        var file = WriteFile("music-old/rock/song.mp3");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "Rock",
                SourceUrl = "stub:pl",
                TargetDirectory = explicitFolder,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                        DownloadStatus = "downloaded", CurrentPath = file,
                    },
                },
            });
            db.ScanLocations.Add(new ScanLocationEntity { Path = Path.Combine(oldRoot, "rock") });
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(oldRoot, newRoot, null, null);

        var rebased = Path.Combine(newRoot, "rock");
        Assert.That(result.Files, Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(rebased, "song.mp3")), Is.True);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.That((await check.Playlists.AsNoTracking().SingleAsync()).TargetDirectory,
            Is.EqualTo(rebased),
            "an explicit folder below the old root follows it, or new downloads recreate the old tree");
        Assert.That((await check.ScanLocations.AsNoTracking().SingleAsync()).Path, Is.EqualTo(rebased));
        Assert.That((await check.Tracks.AsNoTracking().SingleAsync()).CurrentPath,
            Is.EqualTo(Path.Combine(rebased, "song.mp3")));
    }

    [Test]
    public async Task MoveAsync_RemovesEmptyOldFolders_ButKeepsOnesWithFiles()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var file = WriteFile("music-old/My Mix/song.mp3");
        // An untracked file (no row) keeps its folder alive.
        WriteFile("music-old/Notes/readme.txt");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "My Mix",
                SourceUrl = "stub:pl",
                TargetDirectory = oldRoot,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                        DownloadStatus = "downloaded", CurrentPath = file,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        await NewMover().MoveAsync(oldRoot, newRoot, null, null);

        Assert.That(Directory.Exists(Path.Combine(oldRoot, "My Mix")), Is.False,
            "the emptied playlist folder is gone");
        Assert.That(File.Exists(Path.Combine(oldRoot, "Notes", "readme.txt")), Is.True,
            "a folder with anything untracked in it stays");
        Assert.That(Directory.Exists(oldRoot), Is.True, "the root itself is never deleted");
    }

    [Test]
    public async Task MoveAsync_FilesOutsideTheOldRoot_StayPut()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var outside = WriteFile("elsewhere/manual.mp3");

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "m1", Title = "Manual", Artist = "A", Position = 0,
                DownloadStatus = "downloaded", CurrentPath = outside,
            });
            await db.SaveChangesAsync();
        }

        var result = await NewMover().MoveAsync(oldRoot, newRoot, null, null);

        Assert.That(result.Files, Is.EqualTo(0));
        Assert.That(File.Exists(outside), Is.True);
    }
}
