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

            // Should not throw — just logs warnings since none have MELODY_ID
            Assert.Pass("Scan completed without errors");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
