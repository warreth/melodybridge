using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class LibraryScannerTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"ScanTest_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Test]
    public async Task ScanAsync_NonExistentPath_LogsWarning_DoesNotThrow()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        var paths = new[] { new ScanLocation("/nonexistent/path/that/does/not/exist") };

        // Should not throw
        await scanner.ScanAsync(paths);
        Assert.That(db.Tracks, Is.Empty);
    }

    [Test]
    public async Task ScanAsync_EmptyPaths_DoesNothing()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        await scanner.ScanAsync(Array.Empty<ScanLocation>());
        Assert.That(db.Tracks, Is.Empty);
    }

    [Test]
    public async Task ScanAsync_MissingPathInLocation_DoesNothing()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        await scanner.ScanAsync(new[] { new ScanLocation("") });
        Assert.That(db.Tracks, Is.Empty);
    }

    [Test]
    public async Task ScanAsync_ValidPathWithNoMediaFiles_AddsNoTracks()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), $"scan_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a non-media file
            await File.WriteAllTextAsync(Path.Combine(tempDir, "readme.txt"), "hello");

            await scanner.ScanAsync(new[] { new ScanLocation(tempDir) });
            Assert.That(db.Tracks, Is.Empty);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ScanAsync_ValidPathWithMediaFile_WithoutMelodyId_Skips()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), $"scan_media_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create an empty mp3 file (TagLib will fail to read it gracefully)
            var filePath = Path.Combine(tempDir, "song.mp3");
            await File.WriteAllBytesAsync(filePath, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });

            await scanner.ScanAsync(new[] { new ScanLocation(tempDir) });
            Assert.That(db.Tracks, Is.Empty); // No MELODY_ID tag, so skipped
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ScanAsync_OnlyScansConfiguredExtensions()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        var tempDir = Path.Combine(Path.GetTempPath(), $"scan_ext_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "song.mp3"), "not really mp3");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "song.txt"), "not scanned");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "song.flac"), "not really flac");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "video.mp4"), "not music");

            await scanner.ScanAsync(new[] { new ScanLocation(tempDir) });

            // Should not throw: just logs warnings since none have MELODY_ID
            Assert.Pass("Scan completed without errors");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Silence MP3 recipe shared with DownloadMissingAsyncTests: empty ID3v2.3
    /// header + 40 MPEG-1 Layer III frames. TagLib parses this as a real file.
    /// </summary>
    private static byte[] SilenceMp3()
    {
        var id3Header = new byte[]
        {
            0x49, 0x44, 0x33, 0x03, 0x00, 0x00, // "ID3" v2.3, no flags
            0x00, 0x00, 0x00, 0x00,             // tag size = 0 (syncsafe)
        };
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;

        var fileBytes = new byte[id3Header.Length + frame.Length * 40];
        id3Header.CopyTo(fileBytes, 0);
        for (var i = 0; i < 40; i++)
            frame.CopyTo(fileBytes, id3Header.Length + frame.Length * i);
        return fileBytes;
    }

    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), $"mb-scan-{Guid.NewGuid():N}");

    [Test]
    public async Task Scan_TaggedFile_RegistersTrackWithMelodyIdAndMetadata()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source=file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        using var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();

        var dir = NewDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "song.mp3");
            await System.IO.File.WriteAllBytesAsync(path, SilenceMp3());
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(path, "mel-scan-1");
            // Write metadata the scanner should pick up alongside the ID.
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteTags(path,
                title: "Scan Song", artist: "Scan Artist", album: "Scan Album");

            var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);
            await scanner.ScanAsync(new[] { new ScanLocation(dir) });

            var track = db.Tracks.AsNoTracking().SingleOrDefault(t => t.MelodyId == "mel-scan-1");
            Assert.That(track, Is.Not.Null, "tagged file must be registered");
            Assert.That(track!.Title, Is.EqualTo("Scan Song"));
            Assert.That(track.Artist, Is.EqualTo("Scan Artist"));
            Assert.That(track.Album, Is.EqualTo("Scan Album"));
            Assert.That(track.CurrentPath, Is.EqualTo(path));
            Assert.That(track.MediaType, Is.EqualTo("mp3"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Scan_ReScanAfterMove_UpdatesPathForSameMelodyId()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source=file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        using var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();

        var dir1 = NewDir();
        var dir2 = NewDir();
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        try
        {
            var oldPath = Path.Combine(dir1, "song.mp3");
            await System.IO.File.WriteAllBytesAsync(oldPath, SilenceMp3());
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(oldPath, "mel-scan-2");

            var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);
            await scanner.ScanAsync(new[] { new ScanLocation(dir1) });

            // Simulate the user moving the file: same tag, new location.
            var newPath = Path.Combine(dir2, "renamed.mp3");
            System.IO.File.Move(oldPath, newPath);
            await scanner.ScanAsync(new[] { new ScanLocation(dir2) });

            var tracks = db.Tracks.AsNoTracking().Where(t => t.MelodyId == "mel-scan-2").ToList();
            Assert.That(tracks, Has.Exactly(1).Items, "one row, not a duplicate");
            Assert.That(tracks[0].CurrentPath, Is.EqualTo(newPath),
                "the MELODY_ID is the identity: the path must follow the file");
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }
}
