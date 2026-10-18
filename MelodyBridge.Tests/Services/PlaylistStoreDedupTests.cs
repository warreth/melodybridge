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
    public async Task Dedup_SharesTheExistingArchiveCopy_InsteadOfDuplicatingBytes()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();

        // Playlist A's row already has an archive copy, from when it was
        // downloaded. Playlist B dedups to the same track and must point
        // at that copy, not write a second one.
        var archiveDir = Path.Combine(_root, "archive");
        Directory.CreateDirectory(archiveDir);
        var archiveFile = Path.Combine(archiveDir, "Shared Song.mp3");
        File.WriteAllText(archiveFile, "archived bytes");

        var (trackAId, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile);
        await using (var db = _factory.CreateDbContext())
        {
            var a = await db.Tracks.FirstAsync(t => t.Id == trackAId);
            a.ArchivePath = archiveFile;
            var b = await db.Playlists.FirstAsync(p => p.Id == "pl-b");
            b.ArchiveDirectory = archiveDir;
            b.ArchiveFormat = "mp3";
            await db.SaveChangesAsync();
        }

        var store = NewStore(new ThrowingDownloader());
        var (downloaded, failed) = await store.DownloadMissingAsync("pl-b");

        Assert.Multiple(() =>
        {
            Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
            Assert.That(ReadTrack(_factory, trackBId)!.ArchivePath, Is.EqualTo(archiveFile),
                "the deduplicated row shares the original's archive copy");
            Assert.That(Directory.GetFiles(archiveDir), Has.Length.EqualTo(1),
                "no duplicate archive bytes were written");
        });
    }

    [Test]
    public async Task Dedup_DifferentArchiveFormat_MakesThisPlaylistsOwnCopy()
    {
        // The original's archive is an mp3; this playlist wants flac. The
        // bytes must not be linked in, or the green archive check would be
        // green for the wrong format.
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");

        var (dirA, dirB, _) = SetupTwoPlaylists();
        var sourceFile = Path.Combine(dirA, "song.mp3");
        File.WriteAllBytes(sourceFile, TestAudio.MinimalMp3());

        var archiveDir = Path.Combine(_root, "archive");
        Directory.CreateDirectory(archiveDir);
        var aArchive = Path.Combine(archiveDir, "Shared Song.mp3");
        File.Copy(sourceFile, aArchive);

        var (trackAId, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile);
        await using (var db = _factory.CreateDbContext())
        {
            var a = await db.Tracks.FirstAsync(t => t.Id == trackAId);
            a.ArchivePath = aArchive;
            var b = await db.Playlists.FirstAsync(p => p.Id == "pl-b");
            b.ArchiveDirectory = archiveDir;
            b.ArchiveFormat = "flac";
            await db.SaveChangesAsync();
        }

        var store = NewStore(new ThrowingDownloader());
        var (downloaded, failed) = await store.DownloadMissingAsync("pl-b");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)));
        var row = ReadTrack(_factory, trackBId);
        Assert.That(row!.ArchivePath, Is.Not.EqualTo(aArchive),
            "a differently formatted archive is not linked in silently");
        Assert.That(row.ArchivePath, Does.EndWith(".flac"),
            "this playlist gets its own copy in its configured format");
        Assert.That(File.Exists(row.ArchivePath), Is.True);
        Assert.That(new HardLinkService().LinkCount(aArchive), Is.EqualTo(1),
            "the original's archive copy is untouched");
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
    public async Task FlaggedFile_IsNotOfferedForDedup_UntilItsReviewIsSettled()
    {
        var (dirA, dirB, sourceFile) = SetupTwoPlaylists();
        var (trackAId, trackBId) = await SeedSharedTrack(dirA, dirB, sourceFile);
        var store = NewStore(new ThrowingDownloader());

        Assert.That(await store.FindDedupCandidateAsync("spotify:shared-1", trackBId), Is.Not.Null,
            "a settled file is the normal dedup candidate");

        await using (var db = _factory.CreateDbContext())
        {
            var a = db.Tracks.Single(t => t.Id == trackAId);
            a.ReviewKind = TrackReview.Length;
            a.ReviewText = "duration mismatch: 345.2s vs expected 300s";
            await db.SaveChangesAsync();
        }

        Assert.That(await store.FindDedupCandidateAsync("spotify:shared-1", trackBId), Is.Null,
            "linking a file with an open review would fake a user approval that never happened");

        // Settling the review (Keep) makes the file a candidate again.
        await using (var db = _factory.CreateDbContext())
        {
            var a = db.Tracks.Single(t => t.Id == trackAId);
            a.ReviewKind = null;
            a.ReviewText = null;
            await db.SaveChangesAsync();
        }
        Assert.That(await store.FindDedupCandidateAsync("spotify:shared-1", trackBId), Is.Not.Null,
            "an accepted file links like any other");
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
        Assert.That(row.SkipDedup, Is.True,
            "the user's decision travels to the next attempt");
        Assert.That(row.IsHardLink, Is.False);
        Assert.That(row.CurrentPath, Is.Null, "no file claimed yet");
        Assert.That(new HardLinkService().LinkCount(sourceFile), Is.EqualTo(1),
            "playlist A's file was never touched");

        // The download the UI triggers right after the resolve must now
        // actually run: the decision skips the dedup check once.
        var fileToServe = Path.Combine(_root, "serve.flac");
        if (!MakeFlac(fileToServe) || MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffmpeg or ffprobe is not installed on this machine");
        var downloadStore = NewStore(new ServingDownloader(fileToServe));

        var (downloaded, failed) = await downloadStore.DownloadMissingAsync("pl-b");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)),
            "Download new must fetch a fresh file instead of pausing again");
        row = ReadTrack(_factory, trackBId);
        Assert.That(row!.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.That(row.SkipDedup, Is.False, "the one-shot decision is consumed");
        Assert.That(row.IsHardLink, Is.False, "a freshly downloaded file is not a link");
    }

    /// <summary>A real FLAC for the fresh-download path; false without ffmpeg.</summary>
    private static bool MakeFlac(string path)
    {
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) return false;
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-ar", "44100", path,
        })
            psi.ArgumentList.Add(arg);
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return false;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);
        return proc.ExitCode == 0 && File.Exists(path);
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
        public PluginCapabilities Capabilities => PluginCapabilities.Lossy(AudioFormat.Mp3);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
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
        public PluginCapabilities Capabilities => new(
        [
            new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
            new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
        ]);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult(new DownloaderSearchHit(query.Title, query.Artist, "https://serve.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 320));
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            var path = Path.Combine(outputDirectory, Path.GetFileName(file));
            File.Copy(file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }
}
