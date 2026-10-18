using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
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
}
