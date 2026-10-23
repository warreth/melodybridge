using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Library footprint on a real SQLite database and real files on disk: the
/// summary counts main files and archive copies, and an archive scan
/// location reports the copies that live there instead of zero.
/// </summary>
[TestFixture]
[Category("UI")]
public class LibraryFootprintTests
{
    private Bunit.TestContext _ctx = null!;
    private string _root = null!;
    private string _music = null!;
    private string _archive = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _root = Path.Combine(Path.GetTempPath(), $"mb-footprint-{Guid.NewGuid():N}");
        _music = Path.Combine(_root, "music");
        _archive = Path.Combine(_root, "archive");
        Directory.CreateDirectory(Path.Combine(_music, "deep"));
        Directory.CreateDirectory(_archive);

        Write(_music, "a.flac", 1024);
        Write(Path.Combine(_music, "deep"), "b.mp3", 2048);
        Write(_music, "untagged.flac", 4096); // no MELODY_ID row: not library content
        Write(_archive, "a.opus", 512);       // differs from the main copy

        _dbPath = Path.Combine(_root, "library.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "archive_path",
                Value = _archive,
            });
            db.ScanLocations.Add(new ScanLocationEntity { Path = _music });
            db.ScanLocations.Add(new ScanLocationEntity { Path = _archive });
            db.Tracks.AddRange(
                new TrackEntity
                {
                    MelodyId = "a",
                    CurrentPath = Path.Combine(_music, "a.flac"),
                    ArchivePath = Path.Combine(_archive, "a.opus"),
                    FileSizeBytes = 1024,
                    // The app wrote that copy, so it knows the size.
                    ArchiveSizeBytes = 512,
                },
                new TrackEntity
                {
                    MelodyId = "b",
                    CurrentPath = Path.Combine(_music, "deep", "b.mp3"),
                    FileSizeBytes = 2048,
                },
                new TrackEntity
                {
                    CurrentPath = Path.Combine(_music, "untagged.flac"),
                });
            db.SaveChanges();
        }

        var scanner = new Mock<ILibraryScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ScanReport.Empty));
        _ctx.Services.AddSingleton<ILibraryScanner>(scanner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Summary_ShowsFilesSizeAndArchiveCopies()
    {
        var cut = _ctx.Render<Library>();
        var cards = cut.FindAll(".stat-card");
        string Card(string label) => cards
            .Single(c => c.QuerySelector("label")!.TextContent == label)
            .TextContent;

        Assert.Multiple(() =>
        {
            Assert.That(Card("Library files"), Does.Contain("3"),
                "two main files plus one archive copy, the untagged file stays out");
            Assert.That(Card("Library size"), Does.Contain("3.5 KB"));
            Assert.That(Card("Archive copies"), Does.Contain("1"));
            Assert.That(Card("Archive copies"), Does.Contain("0.5 KB in extra copies"));
            Assert.That(Card("Scan folders"), Does.Contain("2"));
            Assert.That(Card("Scan folders"), Does.Contain("1 archive folder"));
        });
    }

    [Test]
    public void ArchiveLocation_CountsItsCopiesInsteadOfZero()
    {
        var cut = _ctx.Render<Library>();
        var card = cut.FindAll(".location-card")
            .Single(c => c.QuerySelector(".path-tag")?.TextContent.Trim() == _archive);

        Assert.Multiple(() =>
        {
            Assert.That(card.TextContent, Does.Contain("1 file with MELODY_ID"),
                "an archive copy belongs to the archive folder, not to zero");
            Assert.That(card.TextContent, Does.Contain("0.5 KB"),
                "the copy is measured on disk, where it differs from the main file");
        });
    }

    [Test]
    public void MainLocation_CountsOnlyItsOwnFilesAndTheirSizes()
    {
        var cut = _ctx.Render<Library>();
        var card = cut.FindAll(".location-card")
            .Single(c => c.QuerySelector(".path-tag")?.TextContent.Trim() == _music);

        Assert.Multiple(() =>
        {
            Assert.That(card.TextContent, Does.Contain("2 files with MELODY_ID"));
            Assert.That(card.TextContent, Does.Contain("3 KB"));
            Assert.That(card.TextContent, Does.Not.Contain("0.5 KB"),
                "the archive copy is not folded into the main folder");
        });
    }

    [Test]
    public async Task AnArchiveCopyWithoutAKnownSize_IsMeasuredOnceAndKept()
    {
        // A copy written before the app knew its size: the page measures
        // it in the background and keeps the answer, so the next visit
        // does not ask the disk again.
        Write(_archive, "c.opus", 512);
        using (var db = _factory.CreateDbContext())
        {
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "c",
                CurrentPath = Path.Combine(_music, "c.flac"),
                FileSizeBytes = 1024,
                ArchivePath = Path.Combine(_archive, "c.opus"),
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();

        // The page measures the copy in the background and keeps the
        // answer, so the next visit does not ask the disk again.
        long? stored = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using (var db = _factory.CreateDbContext())
                stored = db.Tracks.Single(t => t.MelodyId == "c").ArchiveSizeBytes;
            if (stored is > 0) break;
            await Task.Delay(50);
        }
        Assert.That(stored, Is.EqualTo(512),
            "the measured size is kept for the next visit");

        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".stat-card")
            .Single(c => c.QuerySelector("label")!.TextContent == "Archive copies")
            .TextContent,
            Does.Contain("1 KB in extra copies"),
            "the measured size shows up in the summary"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public void SharedPath_CountsOnceWithTheSizeThatIsKnown()
    {
        // Two rows point at one file (a dedup hardlink). The row that
        // comes first predates the size; the second knows it. The file
        // counts once, with the size that is known, and a row without a
        // size never hides it. The file is not on disk, so the background
        // measurement cannot paper over the answer.
        var shared = Path.Combine(_music, "shared.flac");
        using (var db = _factory.CreateDbContext())
        {
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "s1",
                CurrentPath = shared,
            });
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "s2",
                CurrentPath = shared,
                FileSizeBytes = 8192,
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();
        var card = cut.FindAll(".stat-card")
            .Single(c => c.QuerySelector("label")!.TextContent == "Library size");

        Assert.That(card.TextContent, Does.Contain("11.5 KB"),
            "1 KB + 2 KB + 0.5 KB + 8 KB, the shared file counted once with its known size");
    }

    [Test]
    public async Task AMainFileWithoutAKnownSize_IsMeasuredOnceAndKept()
    {
        // A row from before the app stored sizes: the page measures the
        // file in the background, keeps the answer, and the summary shows
        // it. This is the main-file branch of the same write.
        var path = Path.Combine(_music, "legacy.flac");
        File.WriteAllBytes(path, new byte[3072]);
        using (var db = _factory.CreateDbContext())
        {
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "legacy",
                CurrentPath = path,
            });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Library>();

        long? stored = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using (var db = _factory.CreateDbContext())
                stored = db.Tracks.Single(t => t.MelodyId == "legacy").FileSizeBytes;
            if (stored is > 0) break;
            await Task.Delay(50);
        }
        Assert.That(stored, Is.EqualTo(3072),
            "the measured size is stored against the file it came from");

        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".stat-card")
            .Single(c => c.QuerySelector("label")!.TextContent == "Library size")
            .TextContent,
            Does.Contain("6.5 KB"),
            "3.5 KB of known files plus the measured 3 KB"), TimeSpan.FromSeconds(5));
    }

    private static void Write(string dir, string name, int bytes)
        => File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
}
