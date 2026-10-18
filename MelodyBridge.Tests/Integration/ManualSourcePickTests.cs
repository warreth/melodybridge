using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The manual source pick against real SQLite and real files: the store
/// searches every plugin, downloads exactly the picked candidate through
/// the same pipeline as a waterfall download (tags, status, archive) and
/// replaces a wrong file name-wise, refusing when the old bytes cannot go.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class ManualSourcePickTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-pick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "pick.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A plugin whose one hit downloads a real tagged MP3.</summary>
    private sealed class PickPlugin : IDownloader
    {
        public string Id => "pick";
        public string Name => "Pick plugin";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public string? ServedTitle { get; set; } = "The Real Song";
        public string ServedUrl => "https://pick.example/1";

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(ServedTitle!, "The Artist", ServedUrl, TimeSpan.FromSeconds(210)));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"picked-{melodyId}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private PlaylistStore NewStore(out PickPlugin plugin)
    {
        plugin = new PickPlugin();
        var manager = new DownloadManager(
            new ListDownloaderRegistry(plugin),
            NullLogger<DownloadManager>.Instance);
        return new PlaylistStore(
            _factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance);
    }

    private async Task<int> SeedTrackAsync(
        string status = "pending", string? currentPath = null, string? archivePath = null, string directionName = "Pick")
    {
        await using var db = _factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Id = $"pl-{Guid.NewGuid():N}",
            Name = directionName,
            SourceUrl = "stub:pick",
            TargetDirectory = Path.Combine(_root, directionName),
            PreferredFormat = "auto",
        };
        playlist.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-pick-1",
            Title = "The Real Song",
            Artist = "The Artist",
            Position = 0,
            DurationMs = 210_000,
            DownloadStatus = status,
            CurrentPath = currentPath,
            ArchivePath = archivePath,
        });
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return playlist.Tracks.Single().Id;
    }

    [Test]
    public async Task PickingASource_DownloadsItThroughTheNormalPipeline()
    {
        var trackId = await SeedTrackAsync();
        var store = NewStore(out _);

        var candidates = await store.SearchCandidatesAsync(trackId);
        Assert.That(candidates, Has.Count.EqualTo(1));

        var result = await store.DownloadCandidateAsync(trackId, candidates[0]);

        Assert.That(result, Is.EqualTo("downloaded"));
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Multiple(() =>
        {
            Assert.That(row.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(row.SourcePluginId, Is.EqualTo("pick"), "the picked plugin is recorded");
            Assert.That(row.CurrentPath, Does.Contain("picked-mel-pick-1"),
                "the file lands in the playlist folder");
            Assert.That(File.Exists(row.CurrentPath!), Is.True);
        });

        // The pipeline tagged the file, not just the row.
        using var file = TagLib.File.Create(row.CurrentPath!);
        Assert.That(file.Tag.Title, Is.EqualTo("The Real Song"));
        Assert.That(TaglibHelper.ReadMelodyId(row.CurrentPath!), Is.EqualTo("mel-pick-1"));
    }

    [Test]
    public async Task PickingASource_ReplacesTheWrongFileAndItsArchiveCopy()
    {
        var dir = Path.Combine(_root, "Replace");
        var archiveDir = Path.Combine(_root, "ReplaceArchive");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(archiveDir);
        var oldPrimary = Path.Combine(dir, "wrong-song.mp3");
        var oldArchive = Path.Combine(archiveDir, "wrong-song.flac");
        await File.WriteAllBytesAsync(oldPrimary, TestAudio.MinimalMp3());
        await File.WriteAllBytesAsync(oldArchive, TestAudio.MinimalMp3());
        var trackId = await SeedTrackAsync("downloaded", oldPrimary, oldArchive, "Replace");
        var store = NewStore(out _);

        var candidates = await store.SearchCandidatesAsync(trackId);
        var result = await store.DownloadCandidateAsync(trackId, candidates[0]);

        Assert.That(result, Is.EqualTo("downloaded"));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(oldPrimary), Is.False, "the wrong file goes");
            Assert.That(File.Exists(oldArchive), Is.False, "and its archive copy with it");
        });
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.That(File.Exists(row.CurrentPath!), Is.True);
        Assert.That(row.CurrentPath, Does.Not.Contain("wrong-song"));
    }

    [Test]
    public async Task PickingASource_WhenTheOldFileCannotBeDeleted_Refuses()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("needs POSIX directory permissions");

        var dir = Path.Combine(_root, "Locked");
        Directory.CreateDirectory(dir);
        var oldPrimary = Path.Combine(dir, "wrong-song.mp3");
        await File.WriteAllBytesAsync(oldPrimary, TestAudio.MinimalMp3());
        var trackId = await SeedTrackAsync("downloaded", oldPrimary, null, "Locked");
        var store = NewStore(out _);
        var candidates = await store.SearchCandidatesAsync(trackId);

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await store.DownloadCandidateAsync(trackId, candidates[0]);

            Assert.That(result, Is.EqualTo("failed"), "replacing without deleting would leave two files");
            await using var db = await _factory.CreateDbContextAsync();
            var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
            Assert.Multiple(() =>
            {
                Assert.That(row.DownloadStatus, Is.EqualTo("failed"));
                Assert.That(row.DownloadError, Does.Contain("could not replace"));
                Assert.That(File.Exists(oldPrimary), Is.True, "the old file is still there, so the row says so");
            });
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task PickingASource_OnAClaimedRow_WaitsForTheRunningBatch()
    {
        var trackId = await SeedTrackAsync("in_progress");
        var store = NewStore(out _);
        var candidates = await store.SearchCandidatesAsync(trackId);

        var result = await store.DownloadCandidateAsync(trackId, candidates[0]);

        Assert.That(result, Is.EqualTo("claimed-by-another-run"));
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.That(row.DownloadStatus, Is.EqualTo("in_progress"), "the running batch keeps its claim");
    }
}
