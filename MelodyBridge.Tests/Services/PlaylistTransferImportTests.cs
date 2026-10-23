using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Services;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The JSON playlist restore. Rows from a file import (spotify:import:)
/// have no live provider: they restore as the snapshot they left, without
/// a network fetch, and a re-import updates instead of duplicating. A row
/// that cannot come back leaves the other rows untouched.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class PlaylistTransferImportTests
{
    private string _root = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PlaylistStore NewStore(string dbName)
    {
        using var db = new TestSqliteFactory(Path.Combine(_root, dbName)).CreateDbContext();
        db.Database.EnsureCreated();
        return new(new TestSqliteFactory(Path.Combine(_root, dbName)),
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new EmptyDownloaderRegistry(),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);
    }

    private static ImportedFile ManualFile(string name = "Summer mix")
        => new("exportify",
        [
            new ImportedPlaylist(name, null,
            [
                new Track
                {
                    Title = "Ride It", Artist = "Regard",
                    SourcePlatform = Platform.Spotify, SyncStatus = SyncStatus.Pending,
                },
                new Track
                {
                    Title = "Breathe", Artist = "Kungs",
                    SourcePlatform = Platform.Spotify, SyncStatus = SyncStatus.Pending,
                },
            ]),
        ]);

    [Test]
    public async Task ManualImports_RestoreAsTheirSnapshot_NoNetwork()
    {
        var source = NewStore("first.db");
        await source.ImportFileAsync(ManualFile(), Path.Combine(_root, "target"));
        var json = await source.ExportAsync();

        var target = NewStore("second.db");
        var restored = await target.ImportAsync(json);

        Assert.That(restored, Is.EqualTo(1));
        await using var db = new TestSqliteFactory(Path.Combine(_root, "second.db")).CreateDbContext();
        var row = await db.Playlists.AsNoTracking().Include(p => p.Tracks).SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.SourceUrl, Does.StartWith("spotify:import:"),
                "the manual import keeps its own identity");
            Assert.That(row.SourcePlatform, Is.EqualTo(Platform.Unknown),
                "an imported snapshot is an imported snapshot on this side too");
            Assert.That(row.Name, Is.EqualTo("Summer mix"));
            Assert.That(row.Tracks.Count, Is.EqualTo(2), "the tracks come back, no fetch");
            Assert.That(row.Tracks.Select(t => t.Title),
                Is.EqualTo(new[] { "Ride It", "Breathe" }));
            Assert.That(row.TargetDirectory, Is.EqualTo(Path.Combine(_root, "target")));
        });
    }

    [Test]
    public async Task Reimport_AnExistingManualSnapshot_UpdatesInsteadOfDuplicating()
    {
        var source = NewStore("first.db");
        var import = NewStore("second.db");
        await source.ImportFileAsync(ManualFile(), Path.Combine(_root, "target"));
        var json = await source.ExportAsync();

        var restoredOnce = await import.ImportAsync(json);
        var restoredTwice = await import.ImportAsync(json);

        await using var db = new TestSqliteFactory(Path.Combine(_root, "second.db")).CreateDbContext();
        var rows = await db.Playlists.AsNoTracking().Include(p => p.Tracks).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(restoredOnce, Is.EqualTo(1));
            Assert.That(restoredTwice, Is.EqualTo(1), "the second import refreshes the snapshot");
            Assert.That(rows, Has.Count.EqualTo(1), "no duplicate snapshot");
            Assert.That(rows[0].Tracks.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task A_RowThatCannot_Fetch_LeavesTheOtherRowsUntouched()
    {
        var source = NewStore("first.db");
        var import = NewStore("second.db");
        await source.ImportFileAsync(ManualFile(), Path.Combine(_root, "target"));
        var json = await source.ExportAsync();
        // A "live" playlist whose provider claims the url but cannot turn
        // it into a real Spotify playlist id.
        var fresh = JsonSerializer.Deserialize<List<PlaylistEntity>>(json)!;
        fresh.Add(new PlaylistEntity
        {
            Id = "pl-broken",
            Name = "Broken source",
            SourceUrl = "spotify:%zzz-not-a-playlist-id",
        });
        var withBroken = JsonSerializer.Serialize(fresh);

        var restored = await import.ImportAsync(withBroken);

        Assert.That(restored, Is.EqualTo(1), "the broken row is skipped, the rest comes back");
        await using var db = new TestSqliteFactory(Path.Combine(_root, "second.db")).CreateDbContext();
        var row = await db.Playlists.AsNoTracking().Include(p => p.Tracks).SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.Name, Is.EqualTo("Summer mix"));
            Assert.That(row.Tracks.Count, Is.EqualTo(2));
        });
    }
}
