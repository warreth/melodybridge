using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The rename pass against real SQLite and real files: it follows the
/// pattern, moves the archive copy with its track, leaves shared or
/// unmanaged paths alone, and never overwrites an existing name.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class FileNameRenamerTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        Directory.CreateDirectory(Path.Combine(_root, "archive"));
        _dbPath = Path.Combine(_root, "library.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        db.DownloaderSettings.AddRange(
            new DownloaderSettingEntity { Key = "music_path", Value = Path.Combine(_root, "music") },
            new DownloaderSettingEntity { Key = "archive_path", Value = Path.Combine(_root, "archive") });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private FileNameRenamer Renamer() => new(
        _factory, new SettingsStore(_factory), NullLogger<FileNameRenamer>.Instance);

    private string Write(string directory, string name)
    {
        var path = Path.Combine(_root, directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private int Seed(string title, string artist, string currentPath,
        string? archivePath = null, string status = "downloaded", int position = 0,
        string? melodyId = null)
    {
        using var db = _factory.CreateDbContext();
        var track = new TrackEntity
        {
            MelodyId = melodyId ?? $"mel-{Guid.NewGuid():N}",
            Title = title,
            Artist = artist,
            DownloadStatus = status,
            CurrentPath = currentPath,
            ArchivePath = archivePath,
            Position = position,
        };
        db.Tracks.Add(track);
        db.SaveChanges();
        return track.Id;
    }

    [Test]
    public async Task Rename_AppliesThePatternAndMovesTheArchiveCopy()
    {
        var main = Write("music", "mel-a.mp3");
        var archive = Write("archive", "mel-a.mp3");
        var first = Seed("Alpha", "Artist A", main, archive);

        var already = Write("music", "Artist B - Beta.mp3");
        var second = Seed("Beta", "Artist B", already);

        var missing = Path.Combine(_root, "music", "gone.mp3");
        var third = Seed("Gamma", "Artist C", missing);

        var renamer = Renamer();
        var before = await renamer.CountAsync();
        var report = await renamer.RenameAsync();

        var newMain = Path.Combine(_root, "music", "Artist A - Alpha.mp3");
        var newArchive = Path.Combine(_root, "archive", "Artist A - Alpha.mp3");
        Assert.Multiple(() =>
        {
            Assert.That(before.ToRename, Is.EqualTo(1));
            Assert.That(before.AlreadyNamed, Is.EqualTo(1));
            Assert.That(before.Missing, Is.EqualTo(1));
            Assert.That(before.ArchiveSkipped, Is.Zero);
            Assert.That(before.Total, Is.EqualTo(3));
            Assert.That(report.Renamed, Is.EqualTo(1));
            Assert.That(report.Skipped, Is.EqualTo(2), "already named and missing files are skipped");
            Assert.That(report.Failed, Is.Zero);
            Assert.That(File.Exists(newMain), Is.True);
            Assert.That(File.Exists(newArchive), Is.True);
            Assert.That(File.Exists(main), Is.False);
            Assert.That(File.Exists(archive), Is.False);
            Assert.That(File.Exists(already), Is.True, "a file already following the pattern stays");
        });

        using var db = _factory.CreateDbContext();
        Assert.Multiple(() =>
        {
            Assert.That(db.Tracks.Single(t => t.Id == first).CurrentPath, Is.EqualTo(newMain));
            Assert.That(db.Tracks.Single(t => t.Id == first).ArchivePath, Is.EqualTo(newArchive));
            Assert.That(db.Tracks.Single(t => t.Id == second).CurrentPath, Is.EqualTo(already));
            Assert.That(db.Tracks.Single(t => t.Id == third).CurrentPath, Is.EqualTo(missing));
        });
    }

    [Test]
    public async Task Rename_UsesTheSavedPattern()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = FileNamePattern.SettingKey,
                Value = "{title} - {artist}",
            });
            db.SaveChanges();
        }
        var main = Write("music", "mel-a.mp3");
        Seed("Alpha", "Artist A", main);

        await Renamer().RenameAsync();

        Assert.That(File.Exists(Path.Combine(_root, "music", "Alpha - Artist A.mp3")), Is.True);
    }

    [Test]
    public async Task Rename_DoesNotOverwriteAnExistingName()
    {
        var occupied = Write("music", "Artist A - Alpha.mp3");
        var main = Write("music", "mel-a.mp3");
        Seed("Alpha", "Artist A", main);

        var report = await Renamer().RenameAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.Renamed, Is.EqualTo(1));
            Assert.That(File.Exists(occupied), Is.True, "the stranger keeps its name");
            Assert.That(File.Exists(Path.Combine(_root, "music", "Artist A - Alpha (2).mp3")), Is.True);
        });
    }

    [Test]
    public async Task Rename_SkipsAPathAnotherRowReferences()
    {
        var shared = Write("music", "mel-shared.mp3");
        Seed("Shared One", "Artist", shared);
        Seed("Shared Two", "Artist", shared);

        var summary = await Renamer().CountAsync();
        var report = await Renamer().RenameAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.Renamed, Is.Zero);
            Assert.That(report.Skipped, Is.EqualTo(2));
            Assert.That(File.Exists(shared), Is.True);
            Assert.That(summary.Shared, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Rename_LeavesFilesOutsideTheManagedFoldersAlone()
    {
        var outside = Path.Combine(_root, "elsewhere", "mel-a.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "x");
        Seed("Alpha", "Artist A", outside);

        var summary = await Renamer().CountAsync();
        var report = await Renamer().RenameAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.Renamed, Is.Zero);
            Assert.That(File.Exists(outside), Is.True);
            Assert.That(summary.Outside, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Rename_RenamesAPathSharedByTheSameSongOnce()
    {
        var shared = Write("music", "mel-shared.mp3");
        Seed("Shared Song", "Artist", shared, melodyId: "mel-shared");
        Seed("Shared Song", "Artist", shared, melodyId: "mel-shared");

        var summary = await Renamer().CountAsync();
        var report = await Renamer().RenameAsync();

        var renamed = Path.Combine(_root, "music", "Artist - Shared Song.mp3");
        using var db = _factory.CreateDbContext();
        var rows = db.Tracks.AsNoTracking().OrderBy(t => t.Id).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(summary.ToRename, Is.EqualTo(2));
            Assert.That(summary.Shared, Is.Zero);
            Assert.That(report.Renamed, Is.EqualTo(2));
            Assert.That(File.Exists(renamed), Is.True);
            Assert.That(File.Exists(shared), Is.False);
            Assert.That(rows.Select(t => t.CurrentPath), Is.All.EqualTo(renamed),
                "every row that shared the path follows the one move");
        });
    }

    [Test]
    public async Task Rename_RenamesASharedArchiveCopyOnce()
    {
        var shared = Write("music", "mel-shared.flac");
        var archive = Write("archive", "mel-shared.flac");
        Seed("Shared Song", "Artist", shared, archive, melodyId: "mel-shared");
        Seed("Shared Song", "Artist", shared, archive, melodyId: "mel-shared");

        var report = await Renamer().RenameAsync();

        var renamed = Path.Combine(_root, "music", "Artist - Shared Song.flac");
        var renamedArchive = Path.Combine(_root, "archive", "Artist - Shared Song.flac");
        using var db = _factory.CreateDbContext();
        var rows = db.Tracks.AsNoTracking().OrderBy(t => t.Id).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(report.Renamed, Is.EqualTo(2));
            Assert.That(File.Exists(renamed), Is.True);
            Assert.That(File.Exists(renamedArchive), Is.True);
            Assert.That(rows.Select(t => t.CurrentPath), Is.All.EqualTo(renamed));
            Assert.That(rows.Select(t => t.ArchivePath), Is.All.EqualTo(renamedArchive));
        });
    }

    [Test]
    public async Task Rename_RenamesTheTrack_WhenTheArchiveCopyIsOutsideTheManagedFolders()
    {
        var main = Write("music", "mel-a.mp3");
        var archive = Path.Combine(_root, "elsewhere", "mel-a.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllText(archive, "x");
        Seed("Alpha", "Artist A", main, archive);

        var summary = await Renamer().CountAsync();
        var report = await Renamer().RenameAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.ToRename, Is.EqualTo(1));
            Assert.That(summary.ArchiveSkipped, Is.EqualTo(1));
            Assert.That(report.Renamed, Is.EqualTo(1));
            Assert.That(report.ArchiveSkipped, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(_root, "music", "Artist A - Alpha.mp3")), Is.True);
            Assert.That(File.Exists(archive), Is.True,
                "the archive copy keeps its old name when its folder is not managed");
        });
    }

    [Test]
    public async Task Rename_KeepsHardlinksIntact()
    {
        var links = new HardLinkService();
        if (!links.IsSupported) Assert.Ignore("hard links are not supported on this filesystem");

        var first = Write("music/List A", "mel-a.flac");
        var second = Path.Combine(_root, "music", "List B", "mel-a.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        links.Create(second, first);
        Assert.That(links.LinkCount(first), Is.EqualTo(2));
        Seed("Song", "Artist", first);
        Seed("Song", "Artist", second);

        var report = await Renamer().RenameAsync();

        var newFirst = Path.Combine(_root, "music", "List A", "Artist - Song.flac");
        var newSecond = Path.Combine(_root, "music", "List B", "Artist - Song.flac");
        Assert.Multiple(() =>
        {
            Assert.That(report.Renamed, Is.EqualTo(2));
            Assert.That(File.Exists(newFirst), Is.True);
            Assert.That(File.Exists(newSecond), Is.True);
            Assert.That(links.SameFile(newFirst, newSecond), Is.True,
                "both names still point at the same bytes");
            Assert.That(links.LinkCount(newFirst), Is.EqualTo(2),
                "renaming one directory entry does not break the hard link");
        });
    }

    [Test]
    public async Task SourceToken_ReportsNothingToDo()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = FileNamePattern.SettingKey,
                Value = "{source}",
            });
            db.SaveChanges();
        }
        var main = Write("music", "mel-a.mp3");
        Seed("Alpha", "Artist A", main);

        var before = await Renamer().CountAsync();
        var report = await Renamer().RenameAsync();

        Assert.Multiple(() =>
        {
            Assert.That(before.ToRename, Is.Zero);
            Assert.That(before.KeepsSourceName, Is.True);
            Assert.That(report.Renamed, Is.Zero);
            Assert.That(File.Exists(main), Is.True);
        });
    }
}
