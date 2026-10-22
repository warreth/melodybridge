using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// A rerun replaces the file a track had before. The delete of that old
/// file takes the same name lock the writers take, so a worker that is
/// writing the same name right now keeps its file: the delete waits
/// until the writer is done. Without the lock the delete can pull the
/// file away while it is being written.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class ReplacedFileDeleteTests
{
    private string _root = null!;
    private string _music = null!;
    private string _archive = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-replace-{Guid.NewGuid():N}");
        _music = Path.Combine(_root, "music");
        _archive = Path.Combine(_root, "archive");
        Directory.CreateDirectory(_music);
        Directory.CreateDirectory(_archive);
        _factory = new TestSqliteFactory(Path.Combine(_root, "replace.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A plugin whose download lands in the playlist folder.</summary>
    private sealed class FilePlugin : IDownloader
    {
        public string Id => "file";
        public string Name => "File plugin";
        public string Description => "";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, "https://file.example/1", TimeSpan.FromSeconds(210)));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"downloaded-{melodyId}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private PlaylistStore NewStore()
        => new(_factory, Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListDownloaderRegistry(new FilePlugin()),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    /// <summary>
    /// One track that already has a primary file and an archive copy, so
    /// the next download has something to replace.
    /// </summary>
    private async Task<(int trackId, string oldPrimary, string oldArchive)> SeedTrackAsync()
    {
        var oldPrimary = Path.Combine(_music, "old-primary.mp3");
        var oldArchive = Path.Combine(_archive, "old-archive.mp3");
        await File.WriteAllBytesAsync(oldPrimary, TestAudio.MinimalMp3());
        await File.WriteAllBytesAsync(oldArchive, TestAudio.MinimalMp3());

        await using var db = _factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Id = "pl-replace",
            Name = "Replace",
            SourceUrl = "stub:replace",
            TargetDirectory = _music,
            ArchiveDirectory = _archive,
            PreferredFormat = "auto",
        };
        playlist.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-replace-1",
            Title = "The Song",
            Artist = "The Artist",
            Position = 0,
            DownloadStatus = "pending",
            CurrentPath = oldPrimary,
            ArchivePath = oldArchive,
        });
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return (playlist.Tracks.Single().Id, oldPrimary, oldArchive);
    }

    [Test]
    public async Task The_Delete_Of_A_Replaced_File_Waits_For_The_Writer_Of_That_Name()
    {
        var (trackId, oldPrimary, oldArchive) = await SeedTrackAsync();
        var store = NewStore();

        // A worker that is writing this name right now holds the name
        // lock. The delete has to wait for it.
        var scope = await PathLockTable.Shared.AcquireAsync(oldArchive);

        var download = Task.Run(() => store.DownloadTrackAsync(trackId));

        try
        {
            // The old primary is deleted first. The archive copy follows
            // right after it, and that one waits for the lock.
            await WaitUntilAsync(() => !File.Exists(oldPrimary));
            await Task.Delay(500);

            Assert.That(File.Exists(oldArchive), Is.True,
                "the file another worker is writing under this name is still there");
        }
        finally
        {
            scope.Dispose();
        }

        var result = await download.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.That(result, Is.EqualTo("downloaded"));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(oldPrimary), Is.False, "the replaced primary is gone");
            Assert.That(File.Exists(oldArchive), Is.False, "and the replaced archive copy with it");
        });
    }

    [Test]
    public async Task The_Delete_Of_A_Replaced_File_Goes_When_Nothing_Is_Writing_That_Name()
    {
        var (trackId, oldPrimary, oldArchive) = await SeedTrackAsync();
        var store = NewStore();

        var result = await store.DownloadTrackAsync(trackId);

        Assert.That(result, Is.EqualTo("downloaded"));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(oldPrimary), Is.False, "the replaced primary is gone");
            Assert.That(File.Exists(oldArchive), Is.False, "and the replaced archive copy with it");
        });
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.That(File.Exists(row.CurrentPath!), Is.True, "the new file is on disk");
    }

    /// <summary>Waits for a condition, with a clear failure when it never comes.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 30000)
    {
        var waited = 0;
        while (!condition())
        {
            if (waited >= timeoutMs)
                Assert.Fail("the download never reached the delete of the replaced file");
            await Task.Delay(50);
            waited += 50;
        }
    }
}
