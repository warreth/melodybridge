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

    private static void Write(string dir, string name, int bytes)
        => File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
}
