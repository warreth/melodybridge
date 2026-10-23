using System.Data.Common;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The scan's database work: an unchanged library is walked but not
/// written (every row used to be rewritten wholesale, including the
/// download-state columns the scan does not own), a row keeps its own
/// file when the same song also exists under another folder, and one bad
/// file can no longer silence every other file through a poisoned change
/// tracker. Real files, real SQLite, counted commands.
/// </summary>
[TestFixture]
public class LibraryScannerScanCostTests
{
    private string _root = null!;
    private ScanRowCounter _counter = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-scan-cost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _counter = new ScanRowCounter();
        LibraryScanner.ResetReconcileClock();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DbContextOptions<MelodyBridgeDbContext> Options()
        => new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source=file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared")
            .AddInterceptors(_counter)
            .Options;

    private static string TaggedFile(string dir, string melodyId, string name = "song.mp3")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, TestAudio.MinimalMp3());
        TaglibHelper.WriteMelodyId(path, melodyId);
        return path;
    }

    private static async Task<TrackEntity> RowOf(MelodyBridgeDbContext db, string melodyId)
        => await db.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == melodyId);

    [Test]
    public async Task SecondPass_OverAnUnchangedLibrary_WritesNothing()
    {
        var dir = Path.Combine(_root, "music");
        var path = TaggedFile(dir, "mel-cost");

        await using var db = new MelodyBridgeDbContext(Options());
        db.Database.EnsureCreated();

        // First pass: the file is new, the row is one insert.
        await new LibraryScanner(db, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(new[] { new ScanLocation(dir) });
        Assert.Multiple(() =>
        {
            Assert.That(_counter.InsertCommands, Is.EqualTo(1), "the new row is one insert");
            Assert.That(_counter.UpdateCommands, Is.EqualTo(0), "no row rewrite on the way in");
        });

        // Second pass over the same files: everything is unchanged.
        _counter.Reset();
        await new LibraryScanner(db, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(new[] { new ScanLocation(dir) });

        var row = await RowOf(db, "mel-cost");
        Assert.Multiple(() =>
        {
            Assert.That(_counter.InsertCommands, Is.EqualTo(0), "no new rows");
            Assert.That(_counter.UpdateCommands, Is.EqualTo(0),
                "an unchanged library is walked and left alone");
            Assert.That(_counter.DeleteCommands, Is.EqualTo(0));
            Assert.That(row.CurrentPath, Is.EqualTo(path));
        });
    }

    [Test]
    public async Task Row_KeepsItsOwnFile_WhenTheSongExistsUnderAnotherFolderToo()
    {
        var folderA = Path.Combine(_root, "musicA");
        var folderB = Path.Combine(_root, "musicB");
        var pathA = TaggedFile(folderA, "mel-twofolders");
        TaggedFile(folderB, "mel-twofolders", "same-song.mp3");

        await using var db = new MelodyBridgeDbContext(Options());
        db.Database.EnsureCreated();
        await new LibraryScanner(db, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(new[] { new ScanLocation(folderA) });

        // The scan walks only the second folder now. The row already owns
        // the first file; a matching MelodyId under another folder does
        // not pull the row there.
        await new LibraryScanner(db, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(new[] { new ScanLocation(folderB) });

        var row = await RowOf(db, "mel-twofolders");
        Assert.Multiple(() =>
        {
            Assert.That(row.CurrentPath, Is.EqualTo(pathA),
                "the row keeps the file it owned: whichever copy enumerates last is not the new truth");
            Assert.That(row.ReviewKind, Is.Null, "no verdict was dropped: the bytes are the same song's");
        });
    }

    [Test]
    public async Task OneBadRow_DoesNotSilenceTheOtherRows()
    {
        var dir = Path.Combine(_root, "music");
        var goodPath = TaggedFile(dir, "mel-good");
        var poisonPath = TaggedFile(dir, "mel-bad", "poison.mp3");

        await using var db = new PoisonOnInsertDb(Options(), "mel-bad");
        db.Database.EnsureCreated();

        await new LibraryScanner(db, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(new[] { new ScanLocation(dir) });

        Assert.Multiple(() =>
        {
            Assert.That(db.Tracks.AsNoTracking().Count(t => t.MelodyId == "mel-bad"), Is.EqualTo(0),
                "the bad row is deferred, not smuggled in");
            var good = db.Tracks.AsNoTracking().Single(t => t.MelodyId == "mel-good");
            Assert.That(good.CurrentPath, Is.EqualTo(goodPath),
                "the other files of the same chunk are not held hostage by the bad row");
        });

        // Once the bad file is gone, a later scan continues cleanly.
        File.Delete(poisonPath);
        await new LibraryScanner(db, NullLogger<LibraryScanner>.Instance)
            .ScanAsync(new[] { new ScanLocation(dir) });
        Assert.That(db.Tracks.AsNoTracking().Count(t => t.MelodyId == "mel-bad"), Is.EqualTo(0));
    }

    /// <summary>A context whose inserts for one melody refuse to save: the
    /// honest stand-in for the constraint failure that used to poison
    /// every later save of the same context.</summary>
    private sealed class PoisonOnInsertDb(DbContextOptions<MelodyBridgeDbContext> options, string poisonMelodyId)
        : MelodyBridgeDbContext(options)
    {
        private readonly string _poison = poisonMelodyId;

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            var row = ChangeTracker.Entries<TrackEntity>()
                .FirstOrDefault(e => e.State is EntityState.Added
                    && e.Entity.MelodyId == _poison);
            if (row is not null)
                throw new InvalidOperationException("the poison row refuses to be saved (test seam)");
            return base.SaveChangesAsync(ct);
        }
    }
}

/// <summary>The scan's write budget, counted on the real commands.</summary>
internal sealed class ScanRowCounter : DbCommandInterceptor
{
    public int InsertCommands { get; private set; }
    public int UpdateCommands { get; private set; }
    public int DeleteCommands { get; private set; }

    public void Reset()
    {
        InsertCommands = UpdateCommands = DeleteCommands = 0;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int commandResult,
        CancellationToken cancellationToken = default)
    {
        Count(command.CommandText);
        return new ValueTask<int>(commandResult);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        // EF's SQL batch ships through a reader command: the statements
        // inside it (INSERT/UPDATE/DELETE) are what the scan must count.
        var text = command.CommandText;
        if (text.StartsWith("SAVEPOINT", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("RELEASE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SELECT rowid FROM", StringComparison.OrdinalIgnoreCase))
        {
            // Count only the data statements of the batch.
            foreach (var line in text.Split(';'))
                Count(line);
        }
        else
        {
            Count(text);
        }
        return new ValueTask<DbDataReader>(result);
    }

    private void Count(string commandText)
    {
        if (commandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)) InsertCommands++;
        else if (commandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) UpdateCommands++;
        else if (commandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) DeleteCommands++;
    }
}
