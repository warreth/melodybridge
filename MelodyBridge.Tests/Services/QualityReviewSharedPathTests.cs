using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Quality-review retry on a row whose flagged file is shared with another
/// row's file (both rows' CurrentPath point at the same bytes when dedup
/// links them into one folder). The other delete paths in the store check
/// that reference: the retry used to strip the sibling's only copy.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class QualityReviewSharedPathTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private PlaylistStore _store = null!;
    private string _shared = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-review-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _shared = Path.Combine(_root, "shared.mp3");
        File.WriteAllBytes(_shared, TestAudio.MinimalMp3());
        _factory = new TestSqliteFactory(Path.Combine(_root, "review.db"));

        await using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var playlistA = new PlaylistEntity
        {
            Id = "pl-a", Name = "Review A", SourceUrl = "stub:a", TargetDirectory = _root,
        };
        playlistA.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-shared-a", Title = "Shared Song", Artist = "Artist", Position = 0,
            DownloadStatus = "downloaded", CurrentPath = _shared,
            ReviewKind = "quality", ReviewText = "flagged for the test",
        });
        var playlistB = new PlaylistEntity
        {
            Id = "pl-b", Name = "Review B", SourceUrl = "stub:b", TargetDirectory = _root,
        };
        playlistB.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-shared-b", Title = "Shared Song", Artist = "Artist", Position = 0,
            DownloadStatus = "downloaded", CurrentPath = _shared,
        });
        db.Playlists.AddRange(playlistA, playlistB);
        await db.SaveChangesAsync();

        _store = TestStore();
    }

    private PlaylistStore TestStore()
        => new(_factory, Array.Empty<ISourceProvider>(),
            new DownloadManager(new EmptyDownloaderRegistry(),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    [Test]
    public async Task Retry_On_A_Shared_File_Refuses_And_Keeps_Everything()
    {
        var a = FreshRow("mel-shared-a");

        var result = await _store.ResolveQualityReviewAsync(a.Id, QualityReviewAction.Retry);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False, "the retry explains itself instead of stripping a shared file");
            Assert.That(File.Exists(_shared), Is.True, "the flagged bytes stay: the sibling still owns them");
        });

        var row = FreshRow("mel-shared-a");
        Assert.Multiple(() =>
        {
            Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"), "the row stays where it was");
            Assert.That(row.ReviewKind, Is.EqualTo("quality"), "the review stays open");
            Assert.That(row.Warning, Does.Contain("shared"), "the row says why the retry did not happen");
        });

        var sibling = FreshRow("mel-shared-b");
        Assert.That(sibling.ReviewKind, Is.Null, "the sibling was never part of this");
    }

    [Test]
    public async Task Retry_On_A_Private_File_Still_Deletes_And_Resets()
    {
        // The reference-free path keeps working: a row whose flagged file
        // belongs to nobody else goes back to pending without the file.
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.MelodyId == "mel-shared-b");
            row.CurrentPath = Path.Combine(_root, "private.mp3");
            row.ReviewKind = "quality";
            row.ReviewText = "mine";
            await db.SaveChangesAsync();
            File.WriteAllBytes(row.CurrentPath, TestAudio.MinimalMp3());
        }

        var b = FreshRow("mel-shared-b");
        var result = await _store.ResolveQualityReviewAsync(b.Id, QualityReviewAction.Retry);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(File.Exists(FreshRow("mel-shared-b").CurrentPath ?? ""), Is.False,
                "the private flagged file goes, the next download replaces it");
            Assert.That(File.Exists(_shared), Is.True, "the shared file is untouched");
        });
    }

    private TrackEntity FreshRow(string melodyId)
    {
        using var db = _factory.CreateDbContext();
        return db.Tracks.AsNoTracking().Single(t => t.MelodyId == melodyId);
    }
}
