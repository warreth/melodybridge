using System.Runtime.CompilerServices;
using System.Text.Json;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The dedup pipeline of PlaylistStore against a real SQLite database
/// and the real filesystem: the same track in two playlists must share
/// bytes through a hard link when the quality matches, pause for a
/// human when it does not, fall back to a download when the link cannot
/// be made, and never corrupt the surviving playlist when one is
/// deleted. No mocks on the store path: a real store, a real database
/// file, a real OS hard link service.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class PlaylistStoreDedupTests
{
    private TestSqliteFactory _factory = null!;
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-dedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "dedup.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A store whose downloader writes a real audio file into the playlist folder.</summary>
    private PlaylistStore NewStore(IDownloader? downloader = null)
    {
        IDownloadManager manager = new DownloadManager(
            downloader is null
                ? new EmptyDownloaderRegistry()
                : new ListDownloaderRegistry(downloader),
            NullLogger<DownloadManager>.Instance);
        return new PlaylistStore(
            _factory,
            Array.Empty<ISourceProvider>(),
            manager,
            NullLogger<PlaylistStore>.Instance);
    }

    /// <summary>Two playlist folders plus a source audio file in the first playlist's folder.</summary>
    private (string dirA, string dirB, string sourceFile) SetupTwoPlaylists(string format = "mp3", int? bitrate = 320)
    {
        var dirA = Path.Combine(_root, "playlistA");
        var dirB = Path.Combine(_root, "playlistB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // A real, small audio file: the dedup path copies no bytes, it
        // links, so the source must exist on disk with a real extension.
        var sourceFile = Path.Combine(dirA, $"song.{format}");
        File.WriteAllText(sourceFile, "real audio bytes would be here");
        return (dirA, dirB, sourceFile);
    }

    /// <summary>Seeds both playlists with the same track, playlist A already holding the file.</summary>
    private async Task<(int trackAId, int trackBId)> SeedSharedTrack(
        string dirA, string dirB, string sourceFile,
        string? formatA = "mp3", int? bitrateA = 320,
        string preferredB = "mp3:320")
    {
        await using var db = _factory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-a",
            Name = "Playlist A",
            SourceUrl = "https://example.com/a",
            TargetDirectory = dirA,
            PreferredFormat = "mp3:320",
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "spotify:shared-1",
                    Title = "Shared Song",
                    Artist = "Artist",
                    Position = 0,
                    DownloadStatus = "downloaded",
                    CurrentPath = sourceFile,
                    MediaType = formatA,
                    Bitrate = bitrateA,
                    IsHardLink = false,
                },
            },
        });
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-b",
            Name = "Playlist B",
            SourceUrl = "https://example.com/b",
            TargetDirectory = dirB,
            PreferredFormat = preferredB,
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "spotify:shared-1",
                    Title = "Shared Song",
                    Artist = "Artist",
                    Position = 0,
                    DownloadStatus = null, // pending: the run must claim it
                    ExternalPlatform = "Spotify",
                    ExternalId = "shared-1",
                },
            },
        });
        await db.SaveChangesAsync();

        var ids = await db.Tracks
            .Where(t => t.MelodyId == "spotify:shared-1")
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync();
        return (ids[0], ids[1]);
    }

    private static TrackEntity? ReadTrack(TestSqliteFactory factory, int id)
    {
        using var db = factory.CreateDbContext();
        return db.Tracks.AsNoTracking().FirstOrDefault(t => t.Id == id);
    }

    [Test]
    public async Task ExactQualityMatch_DownloadsNothing_LinksTheExistingFile()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile);

        // A downloader stub that would fail loudly if asked for bytes:
        // the dedup path must never call it.
        var store = NewStore(new ThrowingDownloader());

        var (downloaded, failed) = await store.DownloadMissingAsync("pl-b");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)),
            "the hardlinked track counts as done, nothing failed");
        var row = ReadTrack(_factory, trackBId);
        Assert.That(row!.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.That(row.IsHardLink, Is.True, "the row must record that it is a link");
        Assert.That(row.CurrentPath, Does.StartWith(dirB),
            "the link lives in playlist B's own folder");
        Assert.That(File.Exists(row.CurrentPath), Is.True, "the link exists on disk");
        // Same inode: the hard link count on the source rose to 2.
        Assert.That(new HardLinkService().LinkCount(sourceFile), Is.EqualTo(2),
            "the original file must now have two names");
    }

    [Test]
    public async Task QualityMismatch_PausesForReviewInsteadOfGuessing()
    {
        // Playlist A holds an mp3 at 320; playlist B wants flac.
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists(format: "mp3");
        var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile, preferredB: "flac");

        var store = NewStore(new ThrowingDownloader());

        var (downloaded, failed) = await store.DownloadMissingAsync("pl-b");

        Assert.That((downloaded, failed), Is.EqualTo((0, 0)),
            "a review-paused track is neither downloaded nor failed");
        var row = ReadTrack(_factory, trackBId);
        Assert.That(row!.DownloadStatus, Is.EqualTo("needs-review"));
        Assert.That(row.Warning, Does.Contain("dedup conflict"),
            "the warning must tell the user what conflicts");
        Assert.That(row.IsHardLink, Is.False, "nothing was linked yet");
        Assert.That(new HardLinkService().LinkCount(sourceFile), Is.EqualTo(1),
            "no link was made while paused");
    }

    [Test]
    public async Task ResolveReview_HardlinkExisting_LinksAndMarksTheRow()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile, preferredB: "flac");
        var store = NewStore(new ThrowingDownloader());
        await store.DownloadMissingAsync("pl-b");

        var resolved = await store.ResolveReviewAsync(trackBId, hardlinkExisting: true);

        Assert.That(resolved, Is.True);
        var row = ReadTrack(_factory, trackBId);
        Assert.That(row!.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.That(row.IsHardLink, Is.True);
        Assert.That(row.CurrentPath, Does.StartWith(dirB));
        Assert.That(File.Exists(row.CurrentPath), Is.True);
        Assert.That(new HardLinkService().LinkCount(sourceFile), Is.EqualTo(2));
        Assert.That(row.Warning, Does.Contain("user accepted"),
            "the row keeps an honest note about the accepted difference");
    }

    [Test]
    public async Task ResolveReview_DownloadNew_LeavesTheLinkAloneAndDownloads()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile, preferredB: "flac");
        var store = NewStore(new ThrowingDownloader());
        await store.DownloadMissingAsync("pl-b"); // pauses as flac-vs-mp3

        var resolved = await store.ResolveReviewAsync(trackBId, hardlinkExisting: false);

        Assert.That(resolved, Is.True, "the pause clears");
        var row = ReadTrack(_factory, trackBId);
        Assert.That(row!.DownloadStatus, Is.EqualTo("pending"),
            "the track returns to the queue for a fresh download");
        Assert.That(row.IsHardLink, Is.False);
        Assert.That(row.CurrentPath, Is.Null, "no file claimed yet");
        Assert.That(new HardLinkService().LinkCount(sourceFile), Is.EqualTo(1),
            "playlist A's file was never touched");
    }

    [Test]
    public async Task CrossDeviceLink_FallsBackToRealDownload()
    {
        // /dev/shm is a different filesystem: the link must fail and the
        // download waterfall must take over. Without a second filesystem
        // the test would assert nothing, so it ignores itself.
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
        { Assert.Ignore("needs a second filesystem: Linux with /dev/shm"); return; }

        var dirB = Path.Combine("/dev/shm", $"mb-dedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirB);
        try
        {
            var dirA = Path.Combine(_root, "playlistA");
            Directory.CreateDirectory(dirA);
            var sourceFile = Path.Combine(dirA, "song.mp3");
            File.WriteAllText(sourceFile, "audio bytes");

            var fileToServe = Path.Combine(_root, "serve.mp3");
            File.WriteAllBytes(fileToServe, TestAudio.MinimalMp3());
            var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile, preferredB: "auto");
            var store = NewStore(new ServingDownloader(fileToServe));

            var (downloaded, failed) = await store.DownloadMissingAsync("pl-b");

            Assert.That((downloaded, failed), Is.EqualTo((1, 0)),
                "the fallback must produce a real downloaded file");
            var row = ReadTrack(_factory, trackBId);
            Assert.That(row!.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(row.IsHardLink, Is.False,
                "a downloaded file is not a link, even after a failed link attempt");
            Assert.That(row.CurrentPath, Does.StartWith(dirB));
            Assert.That(File.Exists(row.CurrentPath), Is.True);
        }
        finally
        {
            try { Directory.Delete(dirB, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DedupDisabled_DownloadsWithoutLinking()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile, preferredB: "auto");

        var fileToServe = Path.Combine(_root, "serve.mp3");
        File.WriteAllBytes(fileToServe, TestAudio.MinimalMp3());
        var store = NewStore(new ServingDownloader(fileToServe));

        // Turn dedup off: the setting the Advanced page writes.
        using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            { Key = PlaylistStore.DedupSettingKey, Value = "false" });
            db.SaveChanges();
        }

        var (downloaded, _) = await store.DownloadMissingAsync("pl-b");

        Assert.That(downloaded, Is.EqualTo(1));
        var row = ReadTrack(_factory, trackBId);
        Assert.That(row!.IsHardLink, Is.False, "no link when the user switched dedup off");
        Assert.That(new HardLinkService().LinkCount(sourceFile), Is.EqualTo(1));
    }

    [Test]
    public async Task RemovingOnePlaylist_KeepsTheOtherPlaylistWhole()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        var (_, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile);
        var store = NewStore(new ThrowingDownloader());
        await store.DownloadMissingAsync("pl-b"); // playlist B now holds a link

        // Delete playlist A WITH its files: the bytes survive because
        // playlist B holds another name for them, and B's row is intact.
        await store.DeleteAsync("pl-a", deleteFiles: true);

        var row = ReadTrack(_factory, trackBId);
        Assert.That(row, Is.Not.Null, "playlist B's database entry must survive");
        Assert.That(row!.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.That(File.Exists(row.CurrentPath), Is.True,
            "the shared file must survive the other playlist's deletion");
    }

    [Test]
    public async Task SameTrackInTwoPlaylists_BothRowsPersist()
    {
        // The pre-dedup database rejected this outright (unique index on
        // MelodyId). It must be allowed now.
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        await SeedSharedTrack(dirA, dirB, sourceFile);

        using var db = _factory.CreateDbContext();
        var count = await db.Tracks.CountAsync(t => t.MelodyId == "spotify:shared-1");
        Assert.That(count, Is.EqualTo(2),
            "the same source track must live in both playlists");
    }

    /// <summary>A downloader whose every call is a bug in the dedup path.</summary>
    private sealed class ThrowingDownloader : IDownloader
    {
        public string Id => "throwing";
        public string Name => "Should not be called";
        public string Description => "fails when the dedup path wrongly downloads";
        public PluginCapabilities Capabilities => new([AudioFormat.Mp3], null, null, false, true);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => throw new AssertionException("dedup must not search when a link is possible");
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
            => throw new AssertionException("dedup must not download when a link is possible");
    }

    /// <summary>A downloader that copies a prepared file, for the fallback paths.</summary>
    private sealed class ServingDownloader(string file) : IDownloader
    {
        public string Id => "serving";
        public string Name => "Serving";
        public string Description => "copies a prepared file";
        public PluginCapabilities Capabilities => new([AudioFormat.Mp3], null, null, false, true);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult(new DownloaderSearchHit(title, artist, "https://serve.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 320));
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            var path = Path.Combine(outputDirectory, Path.GetFileName(file));
            File.Copy(file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }
}
