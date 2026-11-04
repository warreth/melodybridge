using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The archive copy of a downloaded track: the app writes that file, so it
/// knows how big it is. Remembering the size keeps the Library page from
/// measuring every archive copy on every visit.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class TrackLinkerArchiveSizeTests
{
    private string _dbPath = null!;
    private string _dir = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;
    private TrackLinker _linker = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-archive-size-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-archive-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _settings = new SettingsStore(_factory);
        _linker = new TrackLinker(
            _factory, _settings, new HardLinkService(), NullLogger<TrackLinker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    [Test]
    public async Task TheArchiveCopyIsMeasuredAndRemembered()
    {
        var archiveDir = Path.Combine(_dir, "archive");
        await _settings.SetAsync("archive_path", archiveDir);

        var primary = Path.Combine(_dir, "song.mp3");
        await File.WriteAllBytesAsync(primary, new byte[1234]);

        var track = new TrackEntity
        {
            MelodyId = "melody-1",
            Title = "Song",
            Artist = "A",
            Album = "Al",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = primary,
            PlaylistEntityId = "pl-1",
        };
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "PL",
                SourceUrl = "https://example.com/pl",
                Tracks = new List<TrackEntity> { track },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.MelodyId == "melody-1");
            var outcome = await _linker.ArchiveDownloadedTrackAsync(row, db, default);
            await db.SaveChangesAsync();

            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
                Assert.That(row.ArchivePath, Is.Not.Null.And.Not.Empty);
                Assert.That(File.Exists(row.ArchivePath!), Is.True);
                Assert.That(row.ArchiveSizeBytes, Is.EqualTo(1234),
                    "the app wrote that file, so it knows the size");
            });
        }
    }

    [Test]
    public async Task ExplicitDefaultSelection_WritesTheDefaultArchive()
    {
        var archiveDir = Path.Combine(_dir, "archive-selected");
        await _settings.SetAsync("archive_path", archiveDir);
        var primary = Path.Combine(_dir, "song-selected.mp3");
        await File.WriteAllBytesAsync(primary, new byte[64]);

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-selected",
                Name = "Selected",
                SourceUrl = "https://example.com/selected",
                ArchiveProfileIds = PlaylistArchiveSelection.Serialize(
                    new[] { PlaylistArchiveSelection.DefaultId }),
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "melody-selected",
                        Title = "Song",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = primary,
                        PlaylistEntityId = "pl-selected",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.MelodyId == "melody-selected");
            var outcome = await _linker.ArchiveDownloadedTrackAsync(row, db, default);
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(row.ArchivePath, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public async Task LegacySelection_KeepsWritingToTheOldFolder()
    {
        await _settings.SetAsync("archive_path", Path.Combine(_dir, "archive-global"));
        var legacyDir = Path.Combine(_dir, "archive-legacy");
        var primary = Path.Combine(_dir, "song-legacy.mp3");
        await File.WriteAllBytesAsync(primary, new byte[64]);

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-legacy",
                Name = "Legacy",
                SourceUrl = "https://example.com/legacy",
                ArchiveDirectory = legacyDir,
                ArchiveFormat = "auto",
                ArchiveProfileIds = PlaylistArchiveSelection.Serialize(
                    new[] { PlaylistArchiveSelection.LegacyId }),
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "melody-legacy",
                        Title = "Song",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = primary,
                        PlaylistEntityId = "pl-legacy",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.MelodyId == "melody-legacy");
            var outcome = await _linker.ArchiveDownloadedTrackAsync(row, db, default);
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(row.ArchivePath, Does.StartWith(legacyDir));
        }
    }

    [Test]
    public async Task SelectionWithoutTheDefault_SkipsTheDefaultArchive()
    {
        await _settings.SetAsync("archive_path", Path.Combine(_dir, "archive-unselected"));
        var primary = Path.Combine(_dir, "song-unselected.mp3");
        await File.WriteAllBytesAsync(primary, new byte[64]);

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-unselected",
                Name = "Unselected",
                SourceUrl = "https://example.com/unselected",
                ArchiveProfileIds = PlaylistArchiveSelection.Serialize(new[] { "p-opus" }),
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "melody-unselected",
                        Title = "Song",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = primary,
                        PlaylistEntityId = "pl-unselected",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.MelodyId == "melody-unselected");
            var outcome = await _linker.ArchiveDownloadedTrackAsync(row, db, default);
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Skipped));
            Assert.That(row.ArchivePath, Is.Null);
        }
    }

    [Test]
    public async Task WithoutAnArchiveTarget_NoSizeIsRemembered()
    {
        var primary = Path.Combine(_dir, "song.mp3");
        await File.WriteAllBytesAsync(primary, new byte[10]);
        var track = new TrackEntity
        {
            MelodyId = "melody-2",
            Title = "Song",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = primary,
            PlaylistEntityId = "pl-2",
        };
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-2",
                Name = "PL",
                SourceUrl = "https://example.com/pl2",
                ArchiveEnabled = false,
                Tracks = new List<TrackEntity> { track },
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.MelodyId == "melody-2");
            var outcome = await _linker.ArchiveDownloadedTrackAsync(row, db, default);

            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Skipped));
                Assert.That(row.ArchivePath, Is.Null);
                Assert.That(row.ArchiveSizeBytes, Is.Null);
            });
        }
    }
}
