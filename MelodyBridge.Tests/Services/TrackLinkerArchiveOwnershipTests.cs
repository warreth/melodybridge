using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The archive copy against a real filesystem and a real database. A
/// copy lands on a fixed name, so another row can own that name:
/// replacing it would take that track's file away. The copy is refused
/// instead, and the file that is there keeps its bytes.
/// <para>
/// The database is not the only witness. A file that is on the name
/// carries the id of the song it belongs to, so a file of another song
/// is recognised even when the name is spelled with a case the database
/// comparison does not fold.
/// </para>
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

        // The volume decides, not the operating system: macOS can mount
        // case-sensitive volumes and Linux can mount case-insensitive ones.
        var caseInsensitive = FileSystemCase.FoldsCase(_archiveDir);
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

    [Test]
    public async Task TheFileDecidesAboutANameWithANonAsciiLetter()
    {
        // The database comparison folds ASCII letters only, so "Ärger.mp3"
        // and "ärger.mp3" are two names there. On a volume that folds case
        // they are one file, and only the file itself can say so.
        var primary = Path.Combine(_musicDir, "ärger.mp3");
        TaggedMp3("mb-track-1", primary);

        var owned = Path.Combine(_archiveDir, "Ärger.mp3");
        TaggedMp3("mb-other", owned);

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
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

        if (FileSystemCase.FoldsCase(_archiveDir))
        {
            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.Not.EqualTo(ArchiveOutcome.Written),
                    "on a volume that folds case the other song keeps the file");
                Assert.That(TaglibHelper.ReadMelodyId(owned), Is.EqualTo("mb-other"),
                    "the file of the other song still holds its own song");
            });
        }
        else
        {
            Assert.Multiple(() =>
            {
                Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written),
                    "on a volume that keeps the spellings apart the name is free");
                Assert.That(TaglibHelper.ReadMelodyId(owned), Is.EqualTo("mb-other"),
                    "and the other file is untouched anyway");
            });
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

    // ── the file on the name is the witness ────────────────────────

    [Test]
    public async Task ArchiveCopy_RefusesAFileThatCarriesAnotherSongsId()
    {
        // The file on the name belongs to another song. The database does
        // not point at the name, so the name comparison finds nothing and
        // only the file itself says that the bytes are not ours.
        var primary = Path.Combine(_musicDir, "song.mp3");
        TaggedMp3("mb-track-1", primary);

        var owned = Path.Combine(_archiveDir, "song.mp3");
        TaggedMp3("mb-other", owned);
        var bytes = File.ReadAllBytes(owned);

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
        db.Tracks.Add(track);
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "mb-other",
            Title = "Other",
            Artist = "Artist",
            Position = 1,
            DownloadStatus = "downloaded",
            CurrentPath = Path.Combine(_musicDir, "other.mp3"),
            PlaylistEntityId = "pl-1",
        });
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Failed));
            Assert.That(File.ReadAllBytes(owned), Is.EqualTo(bytes),
                "the file that belongs to the other song kept its bytes");
        });
    }

    [Test]
    public async Task ArchiveCopy_ReplacesTheFileThatCarriesThisTracksOwnId()
    {
        // The file on the name is this song's own earlier copy. Replacing
        // it takes nothing away.
        var primary = Path.Combine(_musicDir, "song.mp3");
        TaggedMp3("mb-track-1", primary);

        var target = Path.Combine(_archiveDir, "song.mp3");
        TaggedMp3("mb-track-1", target);

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(File.Exists(target), Is.True);
            Assert.That(TaglibHelper.ReadMelodyId(target), Is.EqualTo("mb-track-1"));
        });
    }

    [Test]
    public async Task ArchiveCopy_ReplacesALeftoverOfASongThatIsGone()
    {
        // The file on the name belongs to a song that was removed from the
        // library. Nothing claims it any more, so the copy goes through.
        var primary = Path.Combine(_musicDir, "song.mp3");
        TaggedMp3("mb-track-1", primary);

        var target = Path.Combine(_archiveDir, "song.mp3");
        TaggedMp3("mb-removed", target);

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(TaglibHelper.ReadMelodyId(target), Is.EqualTo("mb-track-1"));
        });
    }

    [Test]
    public async Task TwoWorkersOnTheSameArchiveName_LeaveOneFile()
    {
        // Eight tracks in eight folders with the same file name, so all
        // eight want the same archive file. Eight workers copy at the same
        // time.
        var names = new[] { "mb-one", "mb-two", "mb-three", "mb-four",
            "mb-five", "mb-six", "mb-seven", "mb-eight" };
        await using var db = _factory.CreateDbContext();
        foreach (var name in names)
        {
            var folder = Path.Combine(_musicDir, name);
            Directory.CreateDirectory(folder);
            var primary = Path.Combine(folder, "song.mp3");
            TaggedMp3(name, primary);
            db.Tracks.Add(NewTrack(name, primary));
        }
        await db.SaveChangesAsync();

        // Every worker waits for the others, so they all reach the name at
        // the same moment. Without a lock they all pass the check and the
        // last one to move wins.
        var start = new TaskCompletionSource();
        var workers = names.Select(async name =>
        {
            await using var workerDb = _factory.CreateDbContext();
            var track = await workerDb.Tracks.SingleAsync(t => t.MelodyId == name);
            await start.Task;
            return await _linker.ArchiveDownloadedTrackAsync(track, workerDb, CancellationToken.None);
        }).ToList();
        start.SetResult();

        var outcomes = await Task.WhenAll(workers);

        var target = Path.Combine(_archiveDir, "song.mp3");
        var written = outcomes.Count(o => o == ArchiveOutcome.Written);
        var refused = outcomes.Count(o => o == ArchiveOutcome.Failed);
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(1),
                "only one worker may write the name");
            Assert.That(refused, Is.EqualTo(names.Length - 1),
                "the other workers are refused instead of overwriting the file");
            Assert.That(File.Exists(target), Is.True);
            Assert.That(TaglibHelper.ReadMelodyId(target), Is.Not.Null,
                "the file that is there carries the id of the song that wrote it");
        });
    }

    // ── helpers ────────────────────────────────────────────────────

    private static string? _sourceMp3;

    /// <summary>A real MP3 with the given id, made with ffmpeg.</summary>
    private static void TaggedMp3(string melodyId, string path)
    {
        File.Copy(SourceMp3(), path, overwrite: true);
        TaglibHelper.WriteMelodyId(path, melodyId);
        Pad(path);
    }

    /// <summary>
    /// Makes the file big enough that a copy takes a moment. The tag
    /// stays where it is, at the front of the file, and the bytes behind
    /// it are padding. A copy that takes time is what makes two workers
    /// overlap instead of taking turns.
    /// </summary>
    private static void Pad(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.SetLength(stream.Length + 24L * 1024 * 1024);
    }

    /// <summary>One shared MP3 made with ffmpeg.</summary>
    private static string SourceMp3()
    {
        if (_sourceMp3 is not null && File.Exists(_sourceMp3)) return _sourceMp3;
        var ffmpeg = SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");
        var path = Path.Combine(Path.GetTempPath(), $"mb-archive-src-{Guid.NewGuid():N}.mp3");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo "
                + $"-t 5 -c:a libmp3lame -b:a 320k \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(30000);
        if (!File.Exists(path)) Assert.Ignore("ffmpeg did not produce a test file");
        _sourceMp3 = path;
        return path;
    }
}
