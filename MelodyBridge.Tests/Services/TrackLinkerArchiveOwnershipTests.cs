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
    public async Task ArchiveCopy_VerifiedSuccess_ClearsTheOldWarning()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        File.WriteAllText(primary, "primary bytes");

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("track-1", primary);
        track.Warning =
            "archive copy unverified: could not verify the transcode: ffprobe failed";
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(track.Warning, Is.Null, "the verified copy replaces the old verdict");
        });
    }

    [Test]
    public async Task ArchiveCopy_VerifiedAdoption_ClearsTheOldWarning()
    {
        if (SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffprobe is not installed on this machine");

        var primary = Path.Combine(_musicDir, "song.mp3");
        await File.WriteAllBytesAsync(primary, TestAudio.MinimalMp3());
        TaglibHelper.WriteMelodyId(primary, "mb-track-1");

        // A valid same-song copy already sits on the target. The adoption
        // branch checks it and clears the old unverified warning.
        var target = Path.Combine(_archiveDir, "song.mp3");
        await File.WriteAllBytesAsync(target, TestAudio.MinimalMp3());
        TaglibHelper.WriteMelodyId(target, "mb-track-1");

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
        track.Warning =
            "archive copy unverified: could not verify the transcode: ffprobe failed";
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(track.ArchivePath, Is.EqualTo(target), "the verified copy is adopted");
            Assert.That(track.Warning, Is.Null, "the verified copy replaces the old verdict");
        });
    }

    [Test]
    public async Task ArchiveCopy_DisabledTarget_ClearsTheOldWarning()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        File.WriteAllText(primary, "primary bytes");

        await using (var setupDb = _factory.CreateDbContext())
        {
            var playlist = await setupDb.Playlists.SingleAsync(p => p.Id == "pl-1");
            playlist.ArchiveEnabled = false;
            await setupDb.SaveChangesAsync();
        }

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("track-1", primary);
        track.Warning =
            "archive copy unverified: could not verify the transcode: ffprobe failed";
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Skipped));
            Assert.That(track.Warning, Is.Null, "the disabled archive no longer claims a problem");
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
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(File.ReadAllText(owned), Is.EqualTo("another track's bytes"),
                "the file that belongs to the other track kept its bytes");
            Assert.That(track.ArchivePath, Is.EqualTo(
                    Path.Combine(_archiveDir, "song (2).mp3")),
                "the copy takes the next free name instead of overwriting or failing");
            Assert.That(track.Warning, Is.Null);
            Assert.That(File.ReadAllText(track.ArchivePath!), Is.EqualTo("primary bytes"));
        });
    }

    [Test]
    public async Task ArchiveCopy_ExistingVariant_IsReusedInsteadOfNumberedAgain()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        File.WriteAllText(primary, "primary bytes");

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

        await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);
        await db.SaveChangesAsync();
        var first = track.ArchivePath;
        Assert.That(first, Is.EqualTo(Path.Combine(_archiveDir, "song (2).mp3")));

        // A second pass sees its own variant and keeps it. Taking a new
        // one every run would fill the folder with "song (3).mp3" and so on.
        await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(track.ArchivePath, Is.EqualTo(first));
            Assert.That(Directory.GetFiles(_archiveDir), Has.Length.EqualTo(2),
                "a repeated pass writes no extra copy");
        });
    }

    [Test]
    public async Task ArchiveCopy_DoesNotTakeAnotherTracksPrimary()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        TaggedMp3("mb-track-1", primary);

        // The archive target is another track's primary, and the file
        // carries this track's id: the same song, a different file. The
        // archive copy must still not replace that row's primary.
        var otherPrimary = Path.Combine(_archiveDir, "song.mp3");
        TaggedMp3("mb-track-1", otherPrimary);
        var bytes = File.ReadAllBytes(otherPrimary);

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
        db.Tracks.Add(track);
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "mb-track-1",
            Title = "Song",
            Artist = "Artist",
            Position = 1,
            DownloadStatus = "downloaded",
            CurrentPath = otherPrimary,
            PlaylistEntityId = "pl-1",
        });
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(File.ReadAllBytes(otherPrimary), Is.EqualTo(bytes),
                "the other track's primary kept its bytes");
            Assert.That(track.ArchivePath, Is.EqualTo(
                    Path.Combine(_archiveDir, "song (2).mp3")),
                "the copy takes the next free name, never the other track's primary");
            Assert.That(track.Warning, Is.Null);
        });
    }

    [Test]
    public async Task ArchiveCopy_SharesTheFileWhenAnotherRowHasTheSameSong()
    {
        var primary = Path.Combine(_musicDir, "song.mp3");
        TaggedMp3("mb-track-1", primary);
        var otherPrimary = Path.Combine(_musicDir, "other", "song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPrimary)!);
        File.Copy(primary, otherPrimary);
        var target = Path.Combine(_archiveDir, "song.mp3");
        File.Copy(primary, target);
        TaglibHelper.WriteMelodyId(target, "mb-track-1");

        await using var db = _factory.CreateDbContext();
        var track = NewTrack("mb-track-1", primary);
        db.Tracks.Add(track);
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "mb-track-1",
            Title = "Song",
            Artist = "Artist",
            Position = 1,
            DownloadStatus = "downloaded",
            CurrentPath = otherPrimary,
            ArchivePath = target,
            PlaylistEntityId = "pl-1",
        });
        await db.SaveChangesAsync();

        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(track.ArchivePath, Is.EqualTo(target),
                "the same song shares the existing archive copy");
            Assert.That(track.Warning, Is.Null.Or.Empty);
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
                Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
                Assert.That(track.ArchivePath, Is.EqualTo(
                        Path.Combine(_archiveDir, "song (2).mp3")),
                    "on a case-insensitive filesystem the other spelling is taken, so the copy takes a variant");
                Assert.That(File.ReadAllText(owned), Is.EqualTo("another track's bytes"));
            });
        }
        else
        {
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written),
                "on a case-sensitive filesystem the name is free");
            Assert.That(track.ArchivePath, Is.EqualTo(Path.Combine(_archiveDir, "song.mp3")),
                "the differently cased name is a separate file");
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
                Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
                Assert.That(track.ArchivePath, Is.Not.EqualTo(owned),
                    "on a volume that folds case the other song keeps its own file");
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

    private static TrackEntity NewTrack(string melodyId, string primary, string? title = null)
        => new()
        {
            MelodyId = melodyId,
            Title = title ?? "Song",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = primary,
            PlaylistEntityId = "pl-1",
        };

    // ── the file on the name is the witness ────────────────────────

    [Test]
    public async Task ArchiveCopy_TakesAVariantWhenTheNameCarriesAnotherSongsId()
    {
        // The file on the name belongs to another song. The database does
        // not point at the name, so the name comparison finds nothing and
        // only the file itself says that the bytes are not ours. This
        // song takes the next free name, and the other song keeps its file.
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
            Assert.That(outcome, Is.EqualTo(ArchiveOutcome.Written));
            Assert.That(File.ReadAllBytes(owned), Is.EqualTo(bytes),
                "the file that belongs to the other song kept its bytes");
            Assert.That(track.ArchivePath, Is.Not.EqualTo(owned));
            Assert.That(TaglibHelper.ReadMelodyId(track.ArchivePath!), Is.EqualTo("mb-track-1"),
                "this song's copy carries its own id");
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
    public async Task TwoWorkersOnTheSameArchiveName_EachSongGetsItsOwnFile()
    {
        // Eight tracks in eight folders with the same file name, so all
        // eight want the same archive name. Eight workers copy at the same
        // time.
        var names = new[] { "mb-one", "mb-two", "mb-three", "mb-four",
            "mb-five", "mb-six", "mb-seven", "mb-eight" };
        var titles = new[] { "Alpha", "Bravo", "Charlie", "Delta",
            "Echo", "Foxtrot", "Golf", "Hotel" };
        await using var db = _factory.CreateDbContext();
        for (var i = 0; i < names.Length; i++)
        {
            var folder = Path.Combine(_musicDir, names[i]);
            Directory.CreateDirectory(folder);
            var primary = Path.Combine(folder, "song.mp3");
            TaggedMp3(names[i], primary);
            db.Tracks.Add(NewTrack(names[i], primary, titles[i]));
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
            var outcome = await _linker.ArchiveDownloadedTrackAsync(
                track, workerDb, CancellationToken.None);
            await workerDb.SaveChangesAsync();
            return (Name: name, Outcome: outcome, Path: track.ArchivePath);
        }).ToList();
        start.SetResult();

        var outcomes = await Task.WhenAll(workers);

        Assert.Multiple(() =>
        {
            Assert.That(outcomes.All(o => o.Outcome == ArchiveOutcome.Written), Is.True,
                "every song gets a copy, under its own name");
            Assert.That(outcomes.Select(o => o.Path).Distinct().Count(),
                Is.EqualTo(names.Length),
                "no two songs share one archive file");
            foreach (var worker in outcomes)
            {
                Assert.That(File.Exists(worker.Path), Is.True, worker.Name);
                Assert.That(TaglibHelper.ReadMelodyId(worker.Path!), Is.EqualTo(worker.Name),
                    $"the copy for {worker.Name} carries its own id");
            }
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
