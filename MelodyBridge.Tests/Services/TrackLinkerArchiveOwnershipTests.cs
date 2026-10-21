using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The archive copy against a real filesystem and a real database. A
/// copy lands on a fixed name, so another row can own that name:
/// replacing it would take that track's file away. The copy is refused
/// instead, and the file that is there keeps its bytes.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class TrackLinkerArchiveOwnershipTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private string _archiveDir = null!;
    private string _musicDir = null!;
    private TrackLinker _linker = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "archive.db");
        _factory = new TestSqliteFactory(_dbPath);
        _archiveDir = Path.Combine(_root, "archive");
        _musicDir = Path.Combine(_root, "music");
        Directory.CreateDirectory(_archiveDir);
        Directory.CreateDirectory(_musicDir);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-1",
            Name = "Playlist One",
            SourceUrl = "https://example.com/one",
            TargetDirectory = _musicDir,
            ArchiveDirectory = _archiveDir,
            ArchiveFormat = "auto",
            ArchiveEnabled = true,
        });
        await db.SaveChangesAsync();

        _linker = new TrackLinker(_factory, new SettingsStore(_factory),
            new HardLinkService(), NullLogger<TrackLinker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task ArchiveCopy_Writes_WhenTheNameIsFree()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        File.WriteAllText(primary, "primary bytes");

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("track-1", primary);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(File.Exists(Path.Combine(_archiveDir, "song.mp3")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(_archiveDir, "song.mp3")), Is.EqualTo("primary bytes"));
        });
    }

    [Test]
    public async Task ArchiveCopy_DoesNotOverwriteAFileAnotherRowOwns()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        File.WriteAllText(primary, "primary bytes");

        // Another track's archive file already sits on the name the copy
        // would land on.
        var owned = Path.Combine(_archiveDir, "song.mp3");
        File.WriteAllText(owned, "another track's bytes");

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("track-1", primary);
        db.Tracks.Add(track);
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "mb-other",
            Title = "Other",
            Artist = "Artist",
            Position = 1,
            DownloadStatus = "downloaded",
            CurrentPath = Path.Combine(_musicDir, "other.mp3"),
            ArchivePath = owned,
            PlaylistEntityId = "pl-1",
        });
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Failed));
            Assert.That(File.ReadAllText(owned), Is.EqualTo("another track's bytes"),
                "the file that belongs to the other track kept its bytes");
            Assert.That(track.Warning, Does.Contain("already owns"),
                "the row says why there is no archive copy");
        });
    }

    [Test]
    public async Task TheOwnershipCheckFollowsTheFilesystemsCaseRules()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        File.WriteAllText(primary, "primary bytes");

        // The other row owns the same name with a different spelling. On
        // Windows and macOS that is the same file; on Linux it is a
        // different one.
        var owned = Path.Combine(_archiveDir, "Song.mp3");
        File.WriteAllText(owned, "another track's bytes");

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("track-1", primary);
        db.Tracks.Add(track);
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "mb-other",
            Title = "Other",
            Artist = "Artist",
            Position = 1,
            DownloadStatus = "downloaded",
            CurrentPath = Path.Combine(_musicDir, "other.mp3"),
            ArchivePath = owned,
            PlaylistEntityId = "pl-1",
        });
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        var caseInsensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        if (caseInsensitive)
        {
            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Failed),
                    "on a case-insensitive filesystem the name is taken");
                Assert.That(File.ReadAllText(owned), Is.EqualTo("another track's bytes"));
            });
        }
        else
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written),
                "on a case-sensitive filesystem the name is free");
            Assert.That(File.ReadAllText(owned), Is.EqualTo("another track's bytes"),
                "and the other file is untouched anyway");
        }
    }

    private static TrackEntity NewTrack(string melodyId, string primary)
        => new()
        {
            MelodyId = melodyId,
            Title = "Song",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = primary,
            PlaylistEntityId = "pl-1",
        };
}
