using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Named archive profiles against a real SQLite file and real folders:
/// every enabled profile gets its own copy, the copy rows follow moves and
/// deletes, and a removed profile does not remove a user's files.
/// </summary>
[TestFixture]
public class ArchiveProfileTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;
    private ArchiveProfileStore _profiles = null!;
    private ArchiveCopyService _copies = null!;
    private string _primary = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "state", "melodybridge.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _factory = new TestSqliteFactory(_dbPath);
        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "playlist_folders",
                Value = "false",
            });
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p1",
                Name = "Mix",
                SourceUrl = "s:1",
                ArchiveEnabled = true,
            });
            await db.SaveChangesAsync();
        }

        _settings = new SettingsStore(_factory);
        _profiles = new ArchiveProfileStore(_factory);
        _copies = new ArchiveCopyService(_factory, _settings,
            NullLogger<ArchiveCopyService>.Instance);

        _primary = Path.Combine(_root, "music", "song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(_primary)!);
        await File.WriteAllBytesAsync(_primary, TestAudio.MinimalMp3());
        await AddTrackAsync(_primary);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task AddTrackAsync(string primary)
    {
        await using var db = _factory.CreateDbContext();
        db.Tracks.Add(new TrackEntity
        {
            Id = 1,
            MelodyId = "mel-1",
            Title = "Song",
            Artist = "Artist",
            CurrentPath = primary,
            DownloadStatus = "downloaded",
            PlaylistEntityId = "p1",
            FileSizeBytes = new FileInfo(primary).Length,
        });
        await db.SaveChangesAsync();
    }

    private async Task<ArchiveProfileEntity> AddProfileAsync(
        string name, string folder, string format = "auto")
    {
        var profile = await _profiles.SaveAsync(new ArchiveProfileEntity
        {
            Name = name,
            RootPath = Path.Combine(_root, folder),
            Format = format,
            Enabled = true,
            Id = "",
        });
        Directory.CreateDirectory(profile.RootPath);
        return profile;
    }

    [Test]
    public async Task Store_SavesListsTogglesAndDeletes()
    {
        var profile = await AddProfileAsync("Opus", "archive-opus", "opus:128");

        var all = await _profiles.GetAllAsync();
        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.EqualTo(1));
            Assert.That(all[0].Name, Is.EqualTo("Opus"));
            Assert.That(all[0].Format, Is.EqualTo("opus:128"));
        });

        await _profiles.SetEnabledAsync(profile.Id, false);
        Assert.That((await _profiles.GetAllAsync())[0].Enabled, Is.False);
        Assert.That(await _profiles.GetEnabledAsync(), Is.Empty);

        await _profiles.DeleteAsync(profile.Id);
        Assert.That(await _profiles.GetAllAsync(), Is.Empty);
    }

    [Test]
    public void Store_RejectsADuplicateFolderAndFormat()
    {
        var root = Path.Combine(_root, "duplicate");
        Directory.CreateDirectory(root);
        Assert.That(async () =>
        {
            await _profiles.SaveAsync(new ArchiveProfileEntity
            {
                Id = "",
                Name = "One",
                RootPath = root,
                Format = "auto",
            });
            await _profiles.SaveAsync(new ArchiveProfileEntity
            {
                Id = "",
                Name = "Two",
                RootPath = root,
                Format = "auto",
            });
        }, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ExtraCopies_AreWrittenForEveryEnabledProfile()
    {
        await AddProfileAsync("Copy A", "archive-a");
        await AddProfileAsync("Copy B", "archive-b");

        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var copies = await db.TrackArchiveCopies.OrderBy(c => c.ProfileId).ToListAsync();
            Assert.That(copies, Has.Count.EqualTo(2));
            Assert.That(copies.All(copy => copy.Path is { Length: > 0 } && File.Exists(copy.Path)), Is.True);
            Assert.That(copies.All(copy => copy.Warning is null), Is.True);
        }
        Assert.That(File.Exists(Path.Combine(_root, "archive-a", "song.mp3")), Is.True);
        Assert.That(File.Exists(Path.Combine(_root, "archive-b", "song.mp3")), Is.True);
    }

    [Test]
    public async Task ExtraCopies_AreReusedWhenTheProfileIsReEnabled()
    {
        var profile = await AddProfileAsync("Copy A", "archive-a");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }
        var copyPath = Path.Combine(_root, "archive-a", "song.mp3");
        var firstWrite = File.GetLastWriteTimeUtc(copyPath);

        await _profiles.SetEnabledAsync(profile.Id, false);
        await _profiles.SetEnabledAsync(profile.Id, true);
        await Task.Delay(20);
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        Assert.That(File.GetLastWriteTimeUtc(copyPath), Is.EqualTo(firstWrite),
            "an existing complete copy is not rewritten");
    }

    [Test]
    public async Task MoveRoot_MovesFilesAndRows()
    {
        var profile = await AddProfileAsync("Copy A", "archive-a");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        var oldRoot = Path.Combine(_root, "archive-a");
        var newRoot = Path.Combine(_root, "archive-moved");
        await _copies.MoveRootAsync(oldRoot, newRoot);

        await using (var db = _factory.CreateDbContext())
        {
            var copy = await db.TrackArchiveCopies.SingleAsync(c => c.ProfileId == profile.Id);
            Assert.That(copy.Path, Is.EqualTo(Path.Combine(newRoot, "song.mp3")));
        }
        Assert.That(File.Exists(Path.Combine(newRoot, "song.mp3")), Is.True);
        Assert.That(File.Exists(Path.Combine(oldRoot, "song.mp3")), Is.False);
    }

    [Test]
    public async Task RenameForTrack_FollowsThePrimaryName()
    {
        await AddProfileAsync("Copy A", "archive-a");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        var renamedPrimary = Path.Combine(_root, "music", "01 - Song.mp3");
        File.Move(_primary, renamedPrimary);
        await using (var db = _factory.CreateDbContext())
        {
            await _copies.RenameForTrackAsync(1, renamedPrimary, db);
            await db.SaveChangesAsync();
        }

        Assert.That(File.Exists(Path.Combine(_root, "archive-a", "01 - Song.mp3")), Is.True);
        await using (var db = _factory.CreateDbContext())
        {
            var copy = await db.TrackArchiveCopies.SingleAsync(c => c.TrackId == 1);
            Assert.That(copy.Path, Does.EndWith("01 - Song.mp3"));
        }
    }

    [Test]
    public async Task DeleteForTrack_RemovesFilesAndRows()
    {
        await AddProfileAsync("Copy A", "archive-a");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var removed = await _copies.DeleteForTrackAsync(1, db, deleteFiles: true);
            await db.SaveChangesAsync();
            Assert.That(removed, Is.EqualTo(1));
        }

        Assert.That(File.Exists(Path.Combine(_root, "archive-a", "song.mp3")), Is.False);
        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(await db.TrackArchiveCopies.CountAsync(), Is.Zero);
        }
    }

    [Test]
    public async Task DeletingTheProfile_KeepsFilesButRemovesRows()
    {
        var profile = await AddProfileAsync("Copy A", "archive-a");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await _profiles.DeleteAsync(profile.Id);

        Assert.That(File.Exists(Path.Combine(_root, "archive-a", "song.mp3")), Is.True,
            "deleting a profile is a configuration change, not a file delete");
        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(await db.TrackArchiveCopies.CountAsync(), Is.Zero);
        }
    }

    [Test]
    public async Task PlaylistArchiveOff_ClearsExtraCopies()
    {
        await AddProfileAsync("Copy A", "archive-a");
        await using (var db = _factory.CreateDbContext())
        {
            var playlist = await db.Playlists.SingleAsync(p => p.Id == "p1");
            playlist.ArchiveEnabled = false;
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(await db.TrackArchiveCopies.CountAsync(), Is.Zero);
        }
        Assert.That(File.Exists(Path.Combine(_root, "archive-a", "song.mp3")), Is.False,
            "the copy was never written while the playlist switch was off");
    }

    [Test]
    public void FormatParser_AcceptsBitrateAndCodec()
    {
        var bitrate = ArchiveCopier.ParseFormat("mp3:160");
        Assert.That(bitrate.Container, Is.EqualTo("mp3"));
        Assert.That(bitrate.BitrateKbps, Is.EqualTo(160));
        Assert.That(bitrate.Codec, Is.Null);

        var codec = ArchiveCopier.ParseFormat("m4a:alac");
        Assert.That(codec.Container, Is.EqualTo("m4a"));
        Assert.That(codec.Codec, Is.EqualTo("alac"));

        var both = ArchiveCopier.ParseFormat("mp3:libmp3lame:160");
        Assert.That(both.Codec, Is.EqualTo("libmp3lame"));
        Assert.That(both.BitrateKbps, Is.EqualTo(160));

        Assert.That(ArchiveCopier.ExtensionsMayCollide("opus", "opus:192"), Is.True,
            "two Opus bitrates still write the same .opus name");
        Assert.That(ArchiveCopier.ExtensionsMayCollide("opus", "mp3"), Is.False,
            "different extensions can share a folder");
        Assert.That(ArchiveCopier.ExtensionsMayCollide("auto", "flac"), Is.True,
            "Auto keeps the primary extension and can collide");
    }
}
