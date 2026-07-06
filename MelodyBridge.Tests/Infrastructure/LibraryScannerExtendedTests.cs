using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class LibraryScannerExtendedTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"LibScanExt_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Test]
    public async Task ScanAsync_WithValidPaths_ProcessesFiles()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        // Create a temp dir with no valid audio files (no MELODY_ID tags = skipped)
        var tempDir = Path.Combine(Path.GetTempPath(), "MelodyBridgeTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create a text file (should be skipped, not an audio extension)
            await File.WriteAllTextAsync(Path.Combine(tempDir, "readme.txt"), "hello");
            // Create a file with audio extension but no real audio content
            var fakeAudio = Path.Combine(tempDir, "song.mp3");
            await File.WriteAllBytesAsync(fakeAudio, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });

            var paths = new[] { new ScanLocation(tempDir) };
            await scanner.ScanAsync(paths);

            // No tracks should have been added (no valid MELODY_ID)
            var count = await db.Tracks.CountAsync();
            Assert.That(count, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task ScanAsync_MultiplePaths_ScansAll()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        var dir1 = Path.Combine(Path.GetTempPath(), "MelodyBridgeTest1_" + Guid.NewGuid());
        var dir2 = Path.Combine(Path.GetTempPath(), "MelodyBridgeTest2_" + Guid.NewGuid());
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir1, "track.mp3"), new byte[] { 0xFF, 0xFB });
            await File.WriteAllBytesAsync(Path.Combine(dir2, "song.flac"), new byte[] { 0x66, 0x4C, 0x61, 0x43 });

            var paths = new[]
            {
                new ScanLocation(dir1),
                new ScanLocation(dir2),
            };
            await scanner.ScanAsync(paths);

            // Both dirs scanned, but no MELODY_ID tags found
            var count = await db.Tracks.CountAsync();
            Assert.That(count, Is.EqualTo(0));
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        }
    }

    [Test]
    public async Task ScanAsync_EmptyPathList_DoesNotThrow()
    {
        using var db = CreateDbContext();
        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);

        Assert.DoesNotThrowAsync(async () =>
            await scanner.ScanAsync(Array.Empty<ScanLocation>()));
    }
}
