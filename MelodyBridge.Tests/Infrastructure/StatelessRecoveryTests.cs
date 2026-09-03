using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The stateless-recovery contract: the database is wiped, the playlist is
/// re-added from its source, and the already-downloaded files on disk must be
/// adopted back without a single new download. Works only when MelodyIds are
/// deterministic: the re-added rows get the same MELODY_IDs the files carry.
///
/// Real SQLite, real MP3 files, real scanner, real reconciler, real store.
/// Only the source provider and the download manager are fakes; the download
/// manager fake counts calls and must stay at zero.
/// </summary>
[TestFixture]
[Category("Integration")]
public class StatelessRecoveryTests
{
    private string _dbPath = null!;
    private string _dir = null!;

    private const string PlaylistUrl = "https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia";

    private static readonly (string Id, string Title, string Artist)[] Songs =
    {
        ("4uLU6hMCjMI75M1A2tKUQC", "Never Gonna Give You Up", "Rick Astley"),
        ("0VjIjW4GlUZAMYd2v05MiR", "Levitating", "Dua Lipa"),
        ("7qiZfU4y0tLoS0n7ZtHpQ7", "Shape of You", "Ed Sheeran"),
    };

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-stateless-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-stateless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { }
        if (_realMp3 is not null)
            try { File.Delete(_realMp3); _realMp3 = null!; } catch { }
    }

    /// <summary>Counting fake: any download attempt fails the contract.</summary>
    private sealed class CountingDownloadManager : IDownloadManager
    {
        public int Calls;
        public Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default)
        { Calls++; return Task.FromResult<string?>(null); }
        public Task<string?> DownloadTrackAsync(string artist, string title, string outputDirectory, string melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        { Calls++; return Task.FromResult<string?>(null); }
        public IReadOnlyList<DownloadProgress> SnapshotProgress() => Array.Empty<DownloadProgress>();
        public string? LastFailure(string melodyId) => null;
    }

    /// <summary>Canned Spotify playlist: same three tracks on every fetch.</summary>
    private sealed class FakeSpotifyProvider : ISourceProvider
    {
        public string Name => "Spotify";
        public Platform Platform => Platform.Spotify;
        public bool CanHandle(string sourceIdentifier)
            => sourceIdentifier.Contains("open.spotify.com", StringComparison.Ordinal);
        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
            => Task.FromResult(new Playlist
            {
                Id = "2O9RpWPbupc8NrX4T5ZXia",
                Name = "Recovery Fixture",
                Tracks = Songs.Select(s => new Track
                {
                    Title = s.Title,
                    Artist = s.Artist,
                    SongID = new SongID(Platform.Spotify, s.Id),
                    PlatformSongID = new SongID(Platform.Spotify, s.Id),
                    SourcePlatform = Platform.Spotify,
                }).ToList(),
            });
        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
    }

    private TestSqliteFactory NewFactory()
    {
        var factory = new TestSqliteFactory(_dbPath);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        return factory;
    }

    private PlaylistStore NewStore(IDbContextFactory<MelodyBridgeDbContext> factory, CountingDownloadManager downloads)
        => new(factory, new[] { new FakeSpotifyProvider() }, downloads, NullLogger<PlaylistStore>.Instance);

    [Test]
    public async Task WipedDb_ReAddedPlaylist_AdoptsFilesWithoutRedownload()
    {
        // ---- 1. First life: add the playlist, "download" real files, tag them.
        string playlistId;
        var downloads = new CountingDownloadManager();
        var factoryA = NewFactory();
        var storeA = NewStore(factoryA, downloads);
        var (first, _) = await storeA.AddOrRefreshDetailedAsync(PlaylistUrl, targetDirectory: _dir);
        playlistId = first.Id;

        await using (var db = await factoryA.CreateDbContextAsync())
        {
            foreach (var row in db.Tracks.Where(t => t.PlaylistEntityId == playlistId).ToList())
            {
                var song = Songs.Single(s => s.Id == row.ExternalId);
                var path = Path.Combine(_dir, $"{song.Artist} - {song.Title}.mp3");
                File.Copy(RealMp3(), path);
                TaglibHelper.WriteMelodyId(path, row.MelodyId!);
                row.CurrentPath = path;
                row.DownloadStatus = "downloaded";
                row.FileSizeBytes = new FileInfo(path).Length;
            }
            await db.SaveChangesAsync();
        }

        // Remember what the first life wrote: the recovery must reproduce it.
        string[] idsInFirstLife;
        await using (var db = await factoryA.CreateDbContextAsync())
        {
            idsInFirstLife = db.Tracks
                .Where(t => t.PlaylistEntityId == playlistId)
                .OrderBy(t => t.ExternalId)
                .Select(t => t.MelodyId!)
                .ToArray();
        }
        Assert.That(idsInFirstLife, Has.Length.EqualTo(3), "the canned playlist has three tracks");
        Assert.That(idsInFirstLife, Is.All.Contains("spotify:"),
            "MapTrack must produce deterministic spotify:{id} MelodyIds for this to work at all");

        // ---- 2. THE WIPE: the whole database file is gone.
        // Connection pooling would resurrect the deleted file's inode: the
        // second factory would silently reuse the old connection's database
        // and "the wipe" would not have happened at all.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { }
        Assert.That(File.Exists(_dbPath), Is.False, "the database file must be gone before the rebuild");

        // ---- 3. Second life: fresh factory, fresh store, same playlist URL.
        var factoryB = NewFactory();
        var storeB = NewStore(factoryB, downloads);
        var (second, isNew) = await storeB.AddOrRefreshDetailedAsync(PlaylistUrl, targetDirectory: _dir);
        Assert.That(isNew, Is.True, "after a wipe the playlist row is new again");

        await using (var db = await factoryB.CreateDbContextAsync())
        {
            var rows = db.Tracks.Where(t => t.PlaylistEntityId == second.Id).ToList();
            Assert.That(rows, Has.Count.EqualTo(3));

            // THE RECOVERY: deterministic ids reproduce the first life's ids.
            Assert.That(rows.OrderBy(t => t.ExternalId).Select(t => t.MelodyId!),
                Is.EqualTo(idsInFirstLife),
                "re-added rows must carry the same MELODY_IDs the files are tagged with");
            Assert.That(rows.Select(t => t.DownloadStatus), Is.All.EqualTo("pending"),
                "a fresh database knows nothing about the downloads");
        }

        // ---- 4. Point the library at the folder: scan, then reconcile.
        await using (var scanDb = await factoryB.CreateDbContextAsync())
        {
            var scanner = new LibraryScanner(scanDb, NullLogger<LibraryScanner>.Instance);
            await scanner.ScanAsync(new[] { new ScanLocation(_dir) });
        }

        var reconciler = new LibraryReconciler(factoryB, NullLogger<LibraryReconciler>.Instance);
        var (relinked, lost) = await reconciler.ReconcileAllAsync();
        Assert.That(lost, Is.EqualTo(0), "every file is still on disk");

        await using (var db = await factoryB.CreateDbContextAsync())
        {
            var rows = db.Tracks.Where(t => t.PlaylistEntityId == second.Id).OrderBy(t => t.ExternalId).ToList();

            foreach (var row in rows)
            {
                var expectedPath = Path.Combine(_dir,
                    $"{Songs.Single(s => s.Id == row.ExternalId).Artist} - {Songs.Single(s => s.Id == row.ExternalId).Title}.mp3");
                Assert.That(row.CurrentPath, Is.EqualTo(expectedPath),
                    $"track {row.ExternalId} must be relinked to its file");
                Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"),
                    $"track {row.ExternalId} must count as downloaded again");
                Assert.That(TaglibHelper.ReadMelodyId(row.CurrentPath!), Is.EqualTo(row.MelodyId),
                    "the file's MELODY_ID tag and the row agree");
            }

            // ---- 5. Adoption, not duplication: three files, three rows.
            Assert.That(db.Tracks.Count(), Is.EqualTo(3),
                "scanner rows must merge into the playlist rows, not duplicate them");
            Assert.That(db.Tracks.GroupBy(t => t.MelodyId).Count(g => g.Count() > 1), Is.EqualTo(0),
                "no duplicate TrackEntity per MelodyId");
        }

        // ---- 6. Zero downloads issued: the proof that nothing re-downloaded.
        Assert.That(downloads.Calls, Is.EqualTo(0),
            "recovery must adopt the existing files, never download again");
        Assert.That(relinked, Is.EqualTo(3), "all three tracks relinked by the reconciler");
    }

    private static string _realMp3 = null!;

    /// <summary>One shared real MP3 made with ffmpeg (real file, real tags).</summary>
    private static string RealMp3()
    {
        if (_realMp3 is not null && File.Exists(_realMp3)) return _realMp3;
        var path = Path.Combine(Path.GetTempPath(), $"mb-stateless-src-{Guid.NewGuid():N}.mp3");
        var ok = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 1 -c:a libmp3lame -b:a 128k \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!.WaitForExit(10000);
        Assert.That(ok && File.Exists(path), Is.True, "ffmpeg must produce the probe mp3");
        _realMp3 = path;
        return path;
    }
}
