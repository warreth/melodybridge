using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Startup reconciliation against a real SQLite database and real tagged
/// files on disk: relink by remembered path, relink by MELODY_ID after a
/// file moved, and mark vanished files pending again.
/// </summary>
[TestFixture]
public class LibraryReconcilerTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-reconcile-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { }
    }

    private async Task<string> SeedPlaylistAsync(Action<List<TrackEntity>> configureTracks)
    {
        var tracks = new List<TrackEntity>();
        configureTracks(tracks);
        await using var db = _factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Name = "Reconcile",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TargetDirectory = _dir,
        };
        playlist.Tracks = tracks;
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return playlist.Id;
    }

    /// <summary>Writes a real MP3 with a MELODY_ID tag into the folder.</summary>
    private string WriteTaggedFile(string subfolder, string melodyId)
    {
        // Normalize the subfolder separators: on Windows a combined path
        // keeps any '/' from the caller, while the reconciler reports
        // platform separators, and the assertion compares the two.
        var dir = Path.Combine(_dir, subfolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{melodyId}.mp3");
        File.Copy(RealMp3(), path);
        TaglibHelper.WriteMelodyId(path, melodyId);
        return path;
    }

    private static string _realMp3 = null!;

    /// <summary>One shared 128 kbps MP3 made with ffmpeg (real file, real tags).</summary>
    private static string RealMp3()
    {
        if (_realMp3 is not null && File.Exists(_realMp3)) return _realMp3;
        var path = Path.Combine(Path.GetTempPath(), $"mb-reconcile-src-{Guid.NewGuid():N}.mp3");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 1 -c:a libmp3lame -b:a 128k \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10000);
        _realMp3 = path;
        return path;
    }

    [Test]
    public async Task TrackWithExistingPath_IsRelinkedAsDownloaded()
    {
        var path = WriteTaggedFile("root", "mb-recon-1");
        var id = await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-1",
            Title = "Here",
            Artist = "A",
            Position = 0,
            DownloadStatus = "in_progress", // app died mid-run
            CurrentPath = path,
        }));

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance);
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        Assert.That(relinked, Is.EqualTo(1));
        Assert.That(lost, Is.EqualTo(0));
        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.First(t => t.MelodyId == "mb-recon-1");
        Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.That(track.DownloadError, Is.Null);
    }

    [Test]
    public async Task TrackWhoseFileMoved_IsRelinkedByMelodyId()
    {
        var oldPath = WriteTaggedFile("old", "mb-recon-2");
        var newPath = WriteTaggedFile("moved/nested", "mb-recon-2"); // same tag, different place
        File.Delete(oldPath);
        var id = await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-2",
            Title = "Mover",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = oldPath,
        }));

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance);
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        Assert.That(relinked, Is.EqualTo(1));
        Assert.That(lost, Is.EqualTo(0));
        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.First(t => t.MelodyId == "mb-recon-2");
        Assert.That(track.CurrentPath, Is.EqualTo(newPath));
        Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
    }

    [Test]
    public async Task TrackWhoseFileVanished_IsMarkedPendingAgain()
    {
        var id = await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-3",
            Title = "Ghost",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = Path.Combine(_dir, "deleted", "ghost.mp3"),
        }));

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance);
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        Assert.That(lost, Is.EqualTo(1));
        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.First(t => t.MelodyId == "mb-recon-3");
        Assert.That(track.DownloadStatus, Is.EqualTo("pending"));
        Assert.That(track.CurrentPath, Is.Null);
        Assert.That(track.Warning, Does.Contain("re-download"));
    }

    [Test]
    public async Task AlreadyDownloadedTrackWithFile_IsLeftAlone()
    {
        var path = WriteTaggedFile("root", "mb-recon-4");
        var id = await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-4",
            Title = "Fine",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = path,
        }));

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance);
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        Assert.That(relinked, Is.EqualTo(0), "nothing to do when the row is already consistent");
        Assert.That(lost, Is.EqualTo(0));
    }

    // ── Cross-playlist linking ──────────────────────────────────────

    /// <summary>A linker wired like production: the real settings row, real hard links.</summary>
    private TrackLinker NewLinker() => new(
        _factory, new SettingsStore(_factory), new HardLinkService(),
        NullLogger<TrackLinker>.Instance);

    [Test]
    public async Task PendingTrack_WithADifferentQualityCopy_StaysPending()
    {
        // Playlist A holds an mp3 at 320; playlist B wants flac. The
        // library pass must not adopt A's file as B's download: the row
        // stays pending for a real download, where the user decides.
        var musicDir = Path.Combine(_dir, "music");
        var dirA = Path.Combine(musicDir, "Playlist A");
        Directory.CreateDirectory(dirA);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("music_path", musicDir);

        var fileA = WriteTaggedFile("music/Playlist A", "mb-recon-diff");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-a",
                Name = "Playlist A",
                SourceUrl = "https://example.com/a",
                SourcePlatform = Platform.Spotify,
                TargetDirectory = dirA,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-diff",
                        Title = "Different Quality",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = fileA,
                        Bitrate = 320,
                        MediaType = "mp3",
                    },
                },
            });
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-b",
                Name = "Playlist B",
                SourceUrl = "https://example.com/b",
                SourcePlatform = Platform.Spotify,
                PreferredFormat = "flac",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-diff",
                        Title = "Different Quality",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "pending",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, settings, NewLinker());
        await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.Tracks.Single(t => t.PlaylistEntityId == "pl-b");
            Assert.Multiple(() =>
            {
                Assert.That(row.DownloadStatus, Is.EqualTo("pending"),
                    "a different quality is a real download decision, not an adoption");
                Assert.That(row.CurrentPath, Is.Null,
                    "the foreign path must not become this playlist's file");
                Assert.That(row.ReviewKind, Is.Null, "the unprompted pass never pauses for review");
                Assert.That(new HardLinkService().LinkCount(fileA), Is.EqualTo(1),
                    "playlist A's file stays untouched");
            });
        }
    }

    [Test]
    public async Task PendingTrack_WithAnotherPlaylistsCopy_IsHardlinkedIntoItsOwnFolder()
    {
        var musicDir = Path.Combine(_dir, "music");
        var dirA = Path.Combine(musicDir, "Playlist A");
        Directory.CreateDirectory(dirA);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("music_path", musicDir);

        var fileA = WriteTaggedFile("music/Playlist A", "mb-recon-shared");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-a",
                Name = "Playlist A",
                SourceUrl = "https://example.com/a",
                SourcePlatform = Platform.Spotify,
                TargetDirectory = dirA,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-shared",
                        Title = "Shared",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = fileA,
                        Bitrate = 320,
                        MediaType = "mp3",
                    },
                },
            });
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-b",
                Name = "Playlist B",
                SourceUrl = "https://example.com/b",
                SourcePlatform = Platform.Spotify,
                // No explicit folder: music path plus a folder named after
                // the playlist, the app-wide default.
                PreferredFormat = "mp3:320",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-shared",
                        Title = "Shared",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "pending",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, settings, NewLinker());
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.Tracks.Single(t => t.PlaylistEntityId == "pl-b");
            var folderB = Path.Combine(musicDir, "Playlist B");
            Assert.Multiple(() =>
            {
                Assert.That(lost, Is.EqualTo(0));
                Assert.That(relinked, Is.GreaterThanOrEqualTo(1));
                Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"),
                    "the pending row adopts the shared bytes");
                Assert.That(row.IsHardLink, Is.True);
                Assert.That(row.CurrentPath, Is.EqualTo(Path.Combine(folderB, "mb-recon-shared.mp3")),
                    "the file lands in this playlist's own folder, not the other's");
                Assert.That(File.Exists(row.CurrentPath), Is.True);
                Assert.That(new HardLinkService().LinkCount(fileA), Is.EqualTo(2),
                    "playlist A's file gained a name, not a copy");
            });
        }
    }

    [Test]
    public async Task OwnFileInThePlaylistFolder_IsAdopted_InsteadOfLinkingTheOtherCopy()
    {
        // The playlist's own file already sits in its folder: adoption is
        // free, and the linker must not delete it to make room for a link
        // to another playlist's copy of the same track.
        var musicDir = Path.Combine(_dir, "music");
        var dirA = Path.Combine(musicDir, "Playlist A");
        Directory.CreateDirectory(dirA);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("music_path", musicDir);

        var fileA = WriteTaggedFile("music/Playlist A", "mb-recon-own");
        var ownFile = WriteTaggedFile("music/Playlist B", "mb-recon-own");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-a",
                Name = "Playlist A",
                SourceUrl = "https://example.com/a",
                SourcePlatform = Platform.Spotify,
                TargetDirectory = dirA,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-own",
                        Title = "Own",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = fileA,
                        Bitrate = 320,
                        MediaType = "mp3",
                    },
                },
            });
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-b",
                Name = "Playlist B",
                SourceUrl = "https://example.com/b",
                SourcePlatform = Platform.Spotify,
                PreferredFormat = "mp3:320",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-own",
                        Title = "Own",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "pending",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, settings, NewLinker());
        await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.Tracks.Single(t => t.PlaylistEntityId == "pl-b");
            Assert.Multiple(() =>
            {
                Assert.That(row.CurrentPath, Is.EqualTo(ownFile),
                    "the playlist's own file wins over a link to another copy");
                Assert.That(row.IsHardLink, Is.False);
                Assert.That(File.Exists(ownFile), Is.True,
                    "the own file must not be deleted to make room for a link");
                Assert.That(new HardLinkService().LinkCount(fileA), Is.EqualTo(1),
                    "playlist A's file stays untouched");
            });
        }
    }

    [Test]
    public async Task ArchiveCopy_IsNotAdoptedAsTheTrack()
    {
        // The archive root sits under the playlist's base folder, so the
        // relink index walks it. The tagged archive must never satisfy a
        // pending track, or a vanished download would count as present.
        var archiveDir = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(archiveDir);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("music_path", _dir);
        await settings.SetAsync("archive_path", archiveDir);
        var archiveFile = WriteTaggedFile("archive", "mb-recon-arch");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-arch",
                Name = "Archive Only",
                SourceUrl = "https://example.com/arch",
                SourcePlatform = Platform.Spotify,
                TargetDirectory = _dir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-arch",
                        Title = "Only an archive",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "pending",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, settings, NewLinker());
        await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.Tracks.Single(t => t.PlaylistEntityId == "pl-arch");
            Assert.Multiple(() =>
            {
                Assert.That(row.DownloadStatus, Is.EqualTo("pending"),
                    "an archive copy is not the track");
                Assert.That(row.CurrentPath, Is.Null);
            });
        }
        Assert.That(File.Exists(archiveFile), Is.True, "the archive file itself stays");
    }

    [Test]
    public async Task PausedDedupReview_IsNotSettledByReconciliation()
    {
        var path = WriteTaggedFile("root", "mb-recon-paused");
        await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-paused",
            Title = "Waiting",
            Artist = "A",
            Position = 0,
            DownloadStatus = "needs-review",
            Warning = "dedup conflict: playlist wants mp3 320 kbps",
            CurrentPath = path,
        }));

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance,
            OldLayoutSettings(), NewLinker());
        await reconciler.ReconcileAllAsync();

        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.First(t => t.MelodyId == "mb-recon-paused");
        Assert.That(track.DownloadStatus, Is.EqualTo("needs-review"),
            "a pending human decision must survive the library pass");
        Assert.That(track.Warning, Does.Contain("dedup conflict"));
    }

    // ── Folder layout: loose files move into the playlist folder ────

    /// <summary>The seeded TargetDirectory equals the music path: the old
    /// implicit default, so the playlist's own folder applies.</summary>
    private SettingsStore OldLayoutSettings()
    {
        var settings = new SettingsStore(_factory);
        settings.SetAsync("music_path", _dir).GetAwaiter().GetResult();
        return settings;
    }

    [Test]
    public async Task LooseFiles_AreMovedIntoThePlaylistFolder()
    {
        var loose = WriteTaggedFile("loose", "mb-recon-move1");
        await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-move1",
            Title = "Loose",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = loose,
        }));

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, OldLayoutSettings());
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        Assert.That((relinked, lost), Is.EqualTo((0, 0)));
        var expected = Path.Combine(_dir, "Reconcile", "mb-recon-move1.mp3");
        Assert.That(File.Exists(loose), Is.False, "the loose file is gone from its old spot");
        Assert.That(File.Exists(expected), Is.True, "the file now lives in the playlist folder");

        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.First(t => t.MelodyId == "mb-recon-move1");
        Assert.That(track.CurrentPath, Is.EqualTo(expected), "the row follows the move");
    }

    [Test]
    public async Task LooseArchiveCopies_AreMovedIntoThePlaylistsArchiveFolder()
    {
        var primary = WriteTaggedFile("loose", "mb-recon-move2");
        var looseArchive = Path.Combine(_dir, "loose-archive", "mb-recon-move2.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(looseArchive)!);
        File.Copy(RealMp3(), looseArchive);

        await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-move2",
            Title = "Loose archive",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = primary,
            ArchivePath = looseArchive,
        }));

        // The archive root doubles as the old default archive path.
        var settings = OldLayoutSettings();
        await settings.SetAsync("archive_path", Path.Combine(_dir, "loose-archive"));

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance, settings);
        await reconciler.ReconcileAllAsync();

        var expectedArchive = Path.Combine(_dir, "loose-archive", "Reconcile", "mb-recon-move2.flac");
        Assert.That(File.Exists(looseArchive), Is.False);
        Assert.That(File.Exists(expectedArchive), Is.True);
        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.First(t => t.MelodyId == "mb-recon-move2");
        Assert.That(track.ArchivePath, Is.EqualTo(expectedArchive));
    }

    [Test]
    public async Task FilesOutsideTheBaseFolder_AreLeftAlone()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"mb-recon-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var outside = Path.Combine(outsideDir, "mb-recon-stay.mp3");
        File.Copy(RealMp3(), outside);
        try
        {
            await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
            {
                MelodyId = "mb-recon-stay",
                Title = "Stays",
                Artist = "A",
                Position = 0,
                DownloadStatus = "downloaded",
                CurrentPath = outside,
            }));

            var reconciler = new LibraryReconciler(
                _factory, NullLogger<LibraryReconciler>.Instance, OldLayoutSettings());
            await reconciler.ReconcileAllAsync();

            Assert.That(File.Exists(outside), Is.True,
                "a file the user keeps outside the music path is not the app's to move");
            await using var db = _factory.CreateDbContext();
            var track = db.Tracks.First(t => t.MelodyId == "mb-recon-stay");
            Assert.That(track.CurrentPath, Is.EqualTo(outside));
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ExplicitPlaylistFolder_IsNotTouchedByTheFolderLayout()
    {
        // music_path elsewhere: the stored TargetDirectory is an explicit
        // choice, so its files stay exactly where they are.
        await new SettingsStore(_factory).SetAsync("music_path", Path.Combine(_dir, "somewhere-else"));
        var loose = WriteTaggedFile("loose", "mb-recon-explicit");
        await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-explicit",
            Title = "Explicit",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = loose,
        }));

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, new SettingsStore(_factory));
        await reconciler.ReconcileAllAsync();

        Assert.That(File.Exists(loose), Is.True);
        await using var db = _factory.CreateDbContext();
        Assert.That(db.Tracks.First(t => t.MelodyId == "mb-recon-explicit").CurrentPath,
            Is.EqualTo(loose));
    }

    [Test]
    public async Task Move_DoesNotOverwriteAnExistingFile_AndKeepsTheRow()
    {
        var loose = WriteTaggedFile("loose", "mb-recon-clash");
        var occupied = Path.Combine(_dir, "Reconcile", "mb-recon-clash.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.Copy(RealMp3(), occupied);

        await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-clash",
            Title = "Clash",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = loose,
        }));

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, OldLayoutSettings());
        await reconciler.ReconcileAllAsync();

        Assert.That(File.Exists(occupied), Is.True, "the existing file is never overwritten");
        Assert.That(File.Exists(loose), Is.True, "the source stays when the destination is taken");
        await using var db = _factory.CreateDbContext();
        Assert.That(db.Tracks.First(t => t.MelodyId == "mb-recon-clash").CurrentPath,
            Is.EqualTo(loose), "the row keeps the path that still holds its file");
    }

    // ── Shared paths: one physical move, every row follows ──────────

    /// <summary>Two playlists on the same base, each holding a row for the
    /// same track pointing at the same files (the v1.1.1 shared-folder
    /// layout: same music path, dedup stored the same path twice).</summary>
    private async Task SeedSharedRowsAsync(string melodyId, string primary, string? archive)
    {
        await using var db = _factory.CreateDbContext();
        foreach (var (id, name) in new[] { ("pl-a", "Playlist A"), ("pl-b", "Playlist B") })
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = id,
                Name = name,
                SourceUrl = "stub:" + id,
                TargetDirectory = _dir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = melodyId,
                        Title = "Shared",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = primary,
                        ArchivePath = archive,
                    },
                },
            });
        }
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task SharedPrimaryPath_MovesOnce_AndStaysPutAcrossPasses()
    {
        var loose = WriteTaggedFile("", "mb-shared-primary");
        await SeedSharedRowsAsync("mb-shared-primary", loose, null);

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, OldLayoutSettings());
        await reconciler.ReconcileAllAsync();

        var expected = Path.Combine(_dir, "Playlist A", "mb-shared-primary.mp3");
        await using (var db = _factory.CreateDbContext())
        {
            var paths = db.Tracks.AsNoTracking().Select(t => t.CurrentPath).ToList();
            Assert.That(paths, Is.All.EqualTo(expected),
                "both rows follow the one physical move");
        }
        Assert.That(File.Exists(expected), Is.True);
        Assert.That(File.Exists(loose), Is.False);

        // Repeated passes must not rename the same file between the two
        // playlist folders; without the shared-path rule each pass moved it
        // to the other playlist and left one row pointing at nothing.
        await reconciler.ReconcileAllAsync();
        await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(db.Tracks.AsNoTracking().Select(t => t.CurrentPath),
                Is.All.EqualTo(expected), "the path is stable across passes");
        }
        Assert.That(Directory.GetFiles(_dir, "mb-shared-primary.mp3", SearchOption.AllDirectories),
            Has.Length.EqualTo(1), "one file, not a growing set of renames");
    }

    [Test]
    public async Task SharedArchivePath_MovesOnce_AndStaysPutAcrossPasses()
    {
        var primary = WriteTaggedFile("", "mb-shared-archive");
        var looseArchive = Path.Combine(_dir, "arc", "mb-shared-archive.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(looseArchive)!);
        File.Copy(RealMp3(), looseArchive);

        await SeedSharedRowsAsync("mb-shared-archive", primary, looseArchive);
        var settings = OldLayoutSettings();
        await settings.SetAsync("archive_path", Path.Combine(_dir, "arc"));

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance, settings);
        await reconciler.ReconcileAllAsync();

        var expected = Path.Combine(_dir, "arc", "Playlist A", "mb-shared-archive.flac");
        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(db.Tracks.AsNoTracking().Select(t => t.ArchivePath),
                Is.All.EqualTo(expected), "both rows follow the archive move");
        }
        Assert.That(File.Exists(expected), Is.True);

        await reconciler.ReconcileAllAsync();
        await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(db.Tracks.AsNoTracking().Select(t => t.ArchivePath),
                Is.All.EqualTo(expected), "the archive path is stable across passes");
        }
        Assert.That(Directory.GetFiles(Path.Combine(_dir, "arc"), "mb-shared-archive.flac", SearchOption.AllDirectories),
            Has.Length.EqualTo(1));
    }

    [Test]
    public async Task PlaylistWithoutItsOwnFolder_RelinksByMelodyId_UnderTheMusicPath()
    {
        // The playlist page normalizes a stored default folder to null on
        // save; the relink index must still cover the music path, or a
        // manual file move becomes an unnecessary re-download for every
        // active playlist after the update.
        var moved = WriteTaggedFile("Reconcile/moved", "mb-recon-null");
        await SeedPlaylistAsync(tracks => tracks.Add(new TrackEntity
        {
            MelodyId = "mb-recon-null",
            Title = "Moved",
            Artist = "A",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = Path.Combine(_dir, "Reconcile", "gone.mp3"),
        }));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Playlists.SingleAsync()).TargetDirectory = null;
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, OldLayoutSettings());
        var (relinked, lost) = await reconciler.ReconcileAllAsync();

        Assert.That((relinked, lost), Is.EqualTo((1, 0)),
            "the file is found under the music path, not queued for re-download");
        await using var check = await _factory.CreateDbContextAsync();
        var track = await check.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mb-recon-null");
        Assert.That(track.CurrentPath, Is.EqualTo(moved));
        Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
    }

    [Test]
    public async Task CurrentPathInsideAnArchiveFolder_IsClearedNotKeptAsDownloaded()
    {
        // A row from before the archive guard can point its primary at an
        // archive file. The fast path must not bless that: an archive copy
        // is not a primary, so the row returns to pending.
        var archiveDir = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(archiveDir);
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("music_path", _dir);
        await settings.SetAsync("archive_path", archiveDir);
        var file = WriteTaggedFile("archive", "mb-recon-inside");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-inside",
                Name = "Inside",
                SourceUrl = "https://example.com/inside",
                SourcePlatform = Platform.Spotify,
                TargetDirectory = _dir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-recon-inside",
                        Title = "Wrong pointer",
                        Artist = "A",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = file,
                        ArchivePath = file,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(
            _factory, NullLogger<LibraryReconciler>.Instance, settings, NewLinker());
        await reconciler.ReconcileAllAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var row = db.Tracks.Single(t => t.PlaylistEntityId == "pl-inside");
            Assert.Multiple(() =>
            {
                Assert.That(row.CurrentPath, Is.Null,
                    "an archive file never stays the primary");
                Assert.That(row.ArchivePath, Is.Null);
                Assert.That(row.DownloadStatus, Is.EqualTo("pending"),
                    "the track can be linked or downloaded again");
                Assert.That(row.Warning, Does.Contain("archive"),
                    "the row says why it was cleared");
            });
        }
        Assert.That(File.Exists(file), Is.True, "the archive file itself stays");
    }

}