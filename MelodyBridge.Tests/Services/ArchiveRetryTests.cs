using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The per-track archive retry (the row's archive action): rebuilds the
/// copy from the primary file, drops the stale failure from the warning,
/// and refuses honestly when there is no primary or no archive target.
/// Real SQLite files and real bytes on disk throughout.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class ArchiveRetryTests
{
    private static PlaylistStore NewStore(IDbContextFactory<MelodyBridgeDbContext> factory)
        => new(factory, Array.Empty<MelodyBridge.Core.ISourceProvider>(),
            new Application.Services.DownloadManager(
                new EmptyDownloaderRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private PlaylistStore _store = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-arc-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "retry.db"));
        await using (var db = _factory.CreateDbContext())
            await db.Database.EnsureCreatedAsync();
        _store = NewStore(_factory);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>One downloaded track with real bytes, and a playlist whose
    /// archive folder is inside the test root.</summary>
    private async Task<int> SeedDownloadedTrackAsync(bool withArchiveTarget = true, bool fileExists = true)
    {
        var primary = Path.Combine(_root, "music", "song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        if (fileExists) await File.WriteAllBytesAsync(primary, new byte[] { 1, 2, 3, 4, 5 });

        await using var db = _factory.CreateDbContext();
        if (withArchiveTarget)
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "archive_path",
                Value = Path.Combine(_root, "archive"),
            });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "archive_format",
                Value = "auto",
            });
        }
        var playlist = new PlaylistEntity
        {
            Id = "pl-arc",
            Name = "Archive retry",
            SourceUrl = "stub:arc",
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "m-arc",
                    Title = "Song",
                    Artist = "Artist",
                    Position = 0,
                    DownloadStatus = "downloaded",
                    CurrentPath = primary,
                    Warning = "archive copy failed: ffmpeg exited 1: boom",
                },
            },
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return db.Tracks.Single(t => t.MelodyId == "m-arc").Id;
    }

    private async Task<TrackEntity> ReadTrackAsync(int id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    [Test]
    public async Task Retry_WritesTheCopy_AndClearsTheArchiveFailureWarning()
    {
        var trackId = await SeedDownloadedTrackAsync();

        var (ok, reason) = await _store.RetryArchiveAsync(trackId);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(reason, Is.Null);
        });
        var track = await ReadTrackAsync(trackId);
        Assert.That(track.ArchivePath, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(track.ArchivePath), Is.True, "the copy really exists on disk");
        Assert.That(await File.ReadAllBytesAsync(track.ArchivePath!), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }),
            "an auto archive is a byte-for-byte copy");
        Assert.That(track.Warning, Is.Null, "the stale archive failure is gone");
    }

    [Test]
    public async Task Retry_ReplacesTheTarget_SoHardlinkedTwinsKeepTheirBytes()
    {
        var trackId = await SeedDownloadedTrackAsync();
        var (first, _) = await _store.RetryArchiveAsync(trackId);
        Assert.That(first, Is.True);

        var track = await ReadTrackAsync(trackId);
        var archive = track.ArchivePath!;
        var twin = Path.Combine(_root, "twin-archive.mp3");
        new MelodyBridge.Infrastructure.Files.HardLinkService().Create(twin, archive);
        Assert.That(new MelodyBridge.Infrastructure.Files.HardLinkService().LinkCount(archive),
            Is.EqualTo(2), "the twin really shares the archive inode");

        await File.WriteAllBytesAsync(track.CurrentPath!, new byte[] { 9, 9, 9 });
        var (second, reason) = await _store.RetryArchiveAsync(trackId);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.True, reason);
            Assert.That(File.ReadAllBytes(track.ArchivePath!), Is.EqualTo(new byte[] { 9, 9, 9 }),
                "the archive was rebuilt from the current primary");
            Assert.That(File.ReadAllBytes(twin), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }),
                "the other name keeps the old bytes instead of being overwritten through the shared inode");
            Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(archive)!, "*.mb-tmp-*"),
                Is.Empty, "the rebuild leaves no temp file behind");
        });
    }

    [Test]
    public async Task Retry_FailedRebuild_KeepsThePreviousCopyOnTheRow()
    {
        var trackId = await SeedDownloadedTrackAsync();
        var (first, _) = await _store.RetryArchiveAsync(trackId);
        Assert.That(first, Is.True);

        var track = await ReadTrackAsync(trackId);
        var archive = track.ArchivePath!;
        var archiveBytes = await File.ReadAllBytesAsync(archive);

        // A rebuild that needs ffmpeg, with ffmpeg hidden: the old copy
        // stays on disk and must stay on the row too.
        await using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Single(s => s.Key == "archive_format").Value = "opus";
            await db.SaveChangesAsync();
        }
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var emptyPath = Path.Combine(_root, "empty-path");
        Directory.CreateDirectory(emptyPath);
        Environment.SetEnvironmentVariable("PATH", emptyPath);
        try
        {
            var (ok, reason) = await _store.RetryArchiveAsync(trackId);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(reason, Does.Contain("previous copy was kept"));
            });
            var after = await ReadTrackAsync(trackId);
            var afterBytes = await File.ReadAllBytesAsync(archive);
            Assert.Multiple(() =>
            {
                Assert.That(after.ArchivePath, Is.EqualTo(archive),
                    "the row still points at the copy that is on disk");
                Assert.That(File.Exists(archive), Is.True);
                Assert.That(afterBytes, Is.EqualTo(archiveBytes));
                Assert.That(after.Warning, Does.Contain("archive copy failed"));
            });

            var impact = await _store.GetDeleteImpactAsync("pl-arc");
            Assert.That(impact.Files, Is.EqualTo(2),
                "deleting the playlist files counts the primary and the kept archive");

            Assert.That(await _store.DeleteAsync("pl-arc", deleteFiles: true), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(track.CurrentPath!), Is.False);
                Assert.That(File.Exists(archive), Is.False,
                    "the kept archive is deleted with the playlist");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Test]
    public async Task Retry_KeepsOtherWarningSegments()
    {
        var trackId = await SeedDownloadedTrackAsync();
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.SingleAsync(t => t.Id == trackId);
            row.Warning = "quality hint: high end fades out; archive copy failed: boom";
            await db.SaveChangesAsync();
        }

        var (ok, _) = await _store.RetryArchiveAsync(trackId);

        Assert.That(ok, Is.True);
        var track = await ReadTrackAsync(trackId);
        Assert.That(track.Warning, Is.EqualTo("quality hint: high end fades out"),
            "only the archive segment is dropped");
    }

    [Test]
    public async Task Retry_Failure_KeepsTheReasonOnTheRow()
    {
        // A text file with a forced transcode format: ffmpeg cannot decode
        // it, so the archive fails and the row says why.
        var trackId = await SeedDownloadedTrackAsync();
        await using (var db = _factory.CreateDbContext())
        {
            var setting = db.DownloaderSettings.Single(s => s.Key == "archive_format");
            setting.Value = "opus";
            await db.SaveChangesAsync();
        }

        var (ok, reason) = await _store.RetryArchiveAsync(trackId);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("archive copy failed"));
        });
        var track = await ReadTrackAsync(trackId);
        Assert.That(track.ArchivePath, Is.Null);
        Assert.That(track.Warning, Does.Contain("archive copy failed"));
    }

    [Test]
    public async Task Retry_WithoutArchiveTarget_ExplainsTheMissingSetup()
    {
        var trackId = await SeedDownloadedTrackAsync(withArchiveTarget: false);

        var (ok, reason) = await _store.RetryArchiveAsync(trackId);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Is.EqualTo("no archive folder is set up for this playlist"));
        });
    }

    [Test]
    public async Task Retry_MissingPrimary_RefusesWithTheRealReason()
    {
        var trackId = await SeedDownloadedTrackAsync(fileExists: false);

        var (ok, reason) = await _store.RetryArchiveAsync(trackId);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("download the track again"));
        });
    }

    [Test]
    public async Task Retry_TrackWithoutDownload_Refuses()
    {
        var trackId = await SeedDownloadedTrackAsync();
        await using (var db = _factory.CreateDbContext())
        {
            var track = await db.Tracks.SingleAsync(t => t.Id == trackId);
            track.DownloadStatus = "pending";
            await db.SaveChangesAsync();
        }

        var (ok, reason) = await _store.RetryArchiveAsync(trackId);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(reason, Is.EqualTo("the track has no downloaded file yet"));
        });
    }

    [Test]
    public void StripArchiveFailures_RemovesEveryArchiveSegment()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DownloadPipeline.StripArchiveFailures(null), Is.Null);
            Assert.That(DownloadPipeline.StripArchiveFailures("archive copy failed: a"), Is.Null);
            Assert.That(DownloadPipeline.StripArchiveFailures("a; archive copy failed: b"),
                Is.EqualTo("a"));
            Assert.That(DownloadPipeline.StripArchiveFailures("archive copy failed: a; b"),
                Is.EqualTo("b"));
            Assert.That(DownloadPipeline.StripArchiveFailures("a; archive copy failed: b; c"),
                Is.EqualTo("a; c"));
        });
    }
}
