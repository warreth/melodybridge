using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// A scan must link the files it finds to their playlist rows right
/// away. Without the reconcile step, a track that exists on disk stays
/// "pending" until the next restart, and the UI promise "a track you
/// already own is never downloaded again" is false in the session that
/// scanned it. Real file, real SQLite.
/// </summary>
[TestFixture]
public class LibraryScanLinkingTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-scanlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "scan.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Scan_LinksAnOwnedFile_WithoutARestart()
    {
        var musicDir = Path.Combine(_root, "music");
        Directory.CreateDirectory(musicDir);

        // A real tagged MP3 carrying the same MELODY_ID as the playlist row.
        var filePath = Path.Combine(musicDir, "owned.mp3");
        await File.WriteAllBytesAsync(filePath, TestAudio.MinimalMp3());
        TaglibHelper.WriteMelodyId(filePath, "mel-scan-link");
        TaglibHelper.WriteTags(filePath, title: "Owned Song", artist: "Artist");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-scan",
                Name = "Scan link",
                SourceUrl = "stub:scan",
                TargetDirectory = musicDir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-scan-link",
                        Title = "Owned Song",
                        Artist = "Artist",
                        DownloadStatus = "pending",
                        Position = 0,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var reconciler = new LibraryReconciler(_factory, NullLogger<LibraryReconciler>.Instance);
        await using var scanDb = _factory.CreateDbContext();
        var scanner = new LibraryScanner(scanDb, NullLogger<LibraryScanner>.Instance, reconciler);

        var report = await scanner.ScanAsync(new[] { new ScanLocation(musicDir) });

        Assert.That(report.TaggedFiles, Is.EqualTo(1), "the scan finds the tagged file");

        await using var verify = _factory.CreateDbContext();
        var track = await verify.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-scan-link");
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"),
                "the scan itself must link the file, not a later restart");
            Assert.That(track.CurrentPath, Is.EqualTo(filePath));
        });
    }
}
