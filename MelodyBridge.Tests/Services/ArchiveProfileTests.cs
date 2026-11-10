using MelodyBridge.Core;
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

    private async Task AddTrackAsync(string primary, int id = 1, string title = "Song")
    {
        await using var db = _factory.CreateDbContext();
        db.Tracks.Add(new TrackEntity
        {
            Id = id,
            MelodyId = $"mel-{id}",
            Title = title,
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
    public async Task Store_DeleteClearsPublishProfileSource()
    {
        var archive = await AddProfileAsync("FLAC", "archive-flac", "auto");
        var store = new SyncRuleProfileStore(_factory);
        var publish = new SyncRuleProfile
        {
            Id = "publish-flac",
            Name = "Jellyfin FLAC",
            Kind = OutputTargetType.JellyfinApi,
            MediaServerProfileId = "jf",
            SourceArchiveProfileId = archive.Id,
        };
        await store.SaveAsync(publish);

        await _profiles.DeleteAsync(archive.Id);

        var loaded = await store.FindAsync(publish.Id);
        Assert.That(loaded!.SourceArchiveProfileId, Is.Null,
            "the publish profile falls back to the primary file");
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
    public async Task ExtraCopies_FollowThePlaylistSelection()
    {
        var opus = await AddProfileAsync("Opus", "archive-opus", "opus:128");
        await AddProfileAsync("MP3", "archive-mp3", "mp3:192");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Single(p => p.Id == "p1").ArchiveProfileIds =
                PlaylistArchiveSelection.Serialize(new[] { opus.Id });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var copies = await db.TrackArchiveCopies.ToListAsync();
            Assert.That(copies, Has.Count.EqualTo(1));
            Assert.That(copies[0].ProfileId, Is.EqualTo(opus.Id));
        }
        Assert.That(Directory.GetFiles(Path.Combine(_root, "archive-opus")), Has.Length.EqualTo(1),
            "the selected profile wrote one copy");
        Assert.That(Directory.GetFiles(Path.Combine(_root, "archive-mp3")), Is.Empty,
            "the unselected profile wrote nothing");
    }

    [Test]
    public async Task ExtraCopies_MultipleSelectedProfiles_WriteThoseCopies()
    {
        var opus = await AddProfileAsync("Opus", "archive-opus", "opus:128");
        var flac = await AddProfileAsync("FLAC", "archive-flac", "flac");
        await AddProfileAsync("MP3", "archive-mp3", "mp3:192");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Single(p => p.Id == "p1").ArchiveProfileIds =
                PlaylistArchiveSelection.Serialize(new[] { opus.Id, flac.Id });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var ids = await db.TrackArchiveCopies.Select(c => c.ProfileId).ToListAsync();
            Assert.That(ids, Is.EquivalentTo(new[] { opus.Id, flac.Id }));
        }
        Assert.That(Directory.GetFiles(Path.Combine(_root, "archive-mp3")), Is.Empty,
            "the unselected profile wrote nothing");
    }

    [Test]
    public async Task ExtraCopies_DeselectedProfile_RemovesItsRowButKeepsTheFile()
    {
        var opus = await AddProfileAsync("Opus", "archive-opus", "opus:128");
        var mp3 = await AddProfileAsync("MP3", "archive-mp3", "mp3:192");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }
        var mp3File = Directory.GetFiles(Path.Combine(_root, "archive-mp3")).Single();

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Single(p => p.Id == "p1").ArchiveProfileIds =
                PlaylistArchiveSelection.Serialize(new[] { opus.Id });
            await db.SaveChangesAsync();
        }
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var ids = await db.TrackArchiveCopies.Select(c => c.ProfileId).ToListAsync();
            Assert.That(ids, Is.EquivalentTo(new[] { opus.Id }));
        }
        Assert.That(File.Exists(mp3File), Is.True,
            "deselecting a profile keeps the existing archive file");
    }

    [Test]
    public async Task ExtraCopies_BackfillOneProfile_KeepsTheOtherRows()
    {
        var opus = await AddProfileAsync("Opus", "archive-opus", "opus:128");
        var mp3 = await AddProfileAsync("MP3", "archive-mp3", "mp3:192");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(
                track, db, save: true, onlyProfileId: opus.Id);
        }

        await using (var db = _factory.CreateDbContext())
        {
            var ids = await db.TrackArchiveCopies.Select(c => c.ProfileId).ToListAsync();
            Assert.That(ids, Is.EquivalentTo(new[] { opus.Id, mp3.Id }),
                "a one-profile backfill must not drop the other profile's row");
        }
    }

    [Test]
    public async Task Backfill_UnselectedProfile_KeepsEveryRowAndReportsNoFailures()
    {
        var opus = await AddProfileAsync("Opus", "archive-opus", "opus:128");
        var mp3 = await AddProfileAsync("MP3", "archive-mp3", "mp3:192");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Single(p => p.Id == "p1").ArchiveProfileIds =
                PlaylistArchiveSelection.Serialize(new[] { opus.Id });
            await db.SaveChangesAsync();
        }

        ArchiveCopyService.BackfillProgress? last = null;
        await _copies.BuildMissingAsync(
            mp3.Id, new InlineProgress<ArchiveCopyService.BackfillProgress>(value => last = value));

        Assert.That(last, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(last!.Failed, Is.Zero,
                "a playlist that does not select the profile is not a failure");
            Assert.That(last.Total, Is.Zero,
                "nothing is missing for a profile this playlist does not select");
            Assert.That(last.Done, Is.Zero);
        });
        await using (var db = _factory.CreateDbContext())
        {
            var ids = await db.TrackArchiveCopies.Select(c => c.ProfileId).ToListAsync();
            Assert.That(ids, Is.EquivalentTo(new[] { opus.Id, mp3.Id }),
                "backfilling an unselected profile must not touch any row");
        }
    }

    [Test]
    public async Task ExtraCopies_EmptySelection_WritesNothing()
    {
        await AddProfileAsync("Opus", "archive-opus", "opus:128");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Single(p => p.Id == "p1").ArchiveProfileIds =
                PlaylistArchiveSelection.Serialize(Array.Empty<string>());
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
    public async Task Store_SaveAddsTheArchiveRootAsAScanLocation()
    {
        var profile = await AddProfileAsync("Opus", "archive-opus", "opus:128");

        await using (var db = _factory.CreateDbContext())
        {
            var locations = await db.ScanLocations.ToListAsync();
            Assert.That(locations, Has.Count.EqualTo(1),
                "the new archive root registers itself in the Library");
            Assert.That(locations[0].Path, Is.EqualTo(profile.RootPath));
            Assert.That(locations[0].LiveMonitoring, Is.False,
                "a bulk archive folder does not need live rescans");
        }

        await _profiles.SaveAsync(profile);
        await using (var db = _factory.CreateDbContext())
        {
            Assert.That(await db.ScanLocations.CountAsync(), Is.EqualTo(1),
                "saving the same root again does not duplicate the location");
        }
    }

    [Test]
    public async Task Store_MovingTheArchiveRootMovesItsScanLocation()
    {
        var profile = await AddProfileAsync("Opus", "archive-opus", "opus:128");
        var newRoot = Path.Combine(_root, "archive-opus-2");
        profile.RootPath = newRoot;

        await _profiles.SaveAsync(profile);

        await using var db = _factory.CreateDbContext();
        var location = await db.ScanLocations.SingleAsync();
        Assert.That(location.Path, Is.EqualTo(newRoot),
            "the location follows the folder instead of leaving a ghost behind");
    }

    [Test]
    public async Task Backfill_WritesEveryMissingCopyAndReportsProgress()
    {
        var profile = await AddProfileAsync("Auto", "archive-auto", "auto");
        var second = Path.Combine(_root, "music", "second.mp3");
        await File.WriteAllBytesAsync(second, TestAudio.MinimalMp3());
        await AddTrackAsync(second, id: 2, title: "Second");

        var reports = new List<ArchiveCopyService.BackfillProgress>();
        await _copies.BuildMissingAsync(profile.Id,
            new InlineProgress<ArchiveCopyService.BackfillProgress>(reports.Add));

        Assert.That(Directory.GetFiles(Path.Combine(_root, "archive-auto")), Has.Length.EqualTo(2),
            "both missing copies land on disk");
        var last = reports.Last();
        Assert.Multiple(() =>
        {
            Assert.That(last.Total, Is.EqualTo(2));
            Assert.That(last.Done, Is.EqualTo(2));
            Assert.That(last.Failed, Is.Zero);
            Assert.That(last.Running, Is.False);
        });
    }

    [Test]
    public async Task StartupRegistration_AddsEveryConfiguredArchiveRoot()
    {
        var profileRoot = Path.Combine(_root, "archive-direct");
        var globalRoot = profileRoot; // the same root twice must register once
        var legacyRoot = Path.Combine(_root, "archive-legacy");
        await using (var db = _factory.CreateDbContext())
        {
            db.ArchiveProfiles.Add(new ArchiveProfileEntity
            {
                Id = "direct",
                Name = "Direct",
                RootPath = profileRoot,
                Format = "auto",
                Enabled = true,
            });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "archive_path", Value = globalRoot });
            db.Playlists.Single(p => p.Id == "p1").ArchiveDirectory = legacyRoot;
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            await ScanLocations.EnsureConfiguredRootsAsync(db);
            await db.SaveChangesAsync();
        }

        await using var check = _factory.CreateDbContext();
        var paths = await check.ScanLocations.Select(location => location.Path).ToListAsync();
        Assert.That(paths, Is.EquivalentTo(new[] { profileRoot, legacyRoot }),
            "the startup pass covers the default, named and legacy roots once");
    }

    [Test]
    public async Task Store_DeleteRemovesTheAutoScanLocation()
    {
        var profile = await AddProfileAsync("Opus", "archive-opus", "opus:128");

        await _profiles.DeleteAsync(profile.Id);

        await using var db = _factory.CreateDbContext();
        Assert.That(await db.ScanLocations.CountAsync(), Is.Zero,
            "a removed archive root must not keep scanning as a music folder");
    }

    [Test]
    public async Task Store_DeleteKeepsASharedArchiveRoot()
    {
        var opus = await AddProfileAsync("Opus", "archive-shared", "opus:128");
        await AddProfileAsync("MP3", "archive-shared", "mp3:192");

        await _profiles.DeleteAsync(opus.Id);

        await using var db = _factory.CreateDbContext();
        Assert.That(await db.ScanLocations.CountAsync(), Is.EqualTo(1),
            "the other profile still writes to this root");
    }

    [Test]
    public async Task Backfill_SkipsCopiesThatAreAlreadyOnDisk()
    {
        var profile = await AddProfileAsync("Auto", "archive-auto", "auto");
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == 1);
            await _copies.ArchiveExtraCopiesAsync(track, db, save: true);
        }

        ArchiveCopyService.BackfillProgress? last = null;
        await _copies.BuildMissingAsync(profile.Id,
            new InlineProgress<ArchiveCopyService.BackfillProgress>(value => last = value));

        Assert.That(last, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(last!.Total, Is.Zero, "the copy is already present");
            Assert.That(last.Done, Is.Zero);
            Assert.That(last.Failed, Is.Zero);
            Assert.That(last.Message, Is.EqualTo("Every archive copy is present."));
        });
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

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }
}
