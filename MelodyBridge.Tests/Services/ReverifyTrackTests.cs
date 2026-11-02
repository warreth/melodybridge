using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Tests;
using Microsoft.EntityFrameworkCore;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The unverified repair re-probes the file that is already on disk. It
/// must clear the warning when ffprobe can read it, and leave the file and
/// the unverified state alone when the probe is not conclusive. The
/// post-download gate may delete broken bytes; a repair on a file the user
/// already has must not.
/// </summary>
[TestFixture]
public class ReverifyTrackTests
{
    private const string UnverifiedWarning =
        "could not verify the download: ffprobe is not installed";

    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-reverify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "state.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PlaylistStore Store()
        => new(_factory,
            Array.Empty<ISourceProvider>(),
            new Mock<IDownloadManager>().Object,
            NullLogger<PlaylistStore>.Instance);

    private AttentionActions Actions(PlaylistStore store)
    {
        var archives = new ArchiveCopyService(
            _factory,
            new SettingsStore(_factory),
            NullLogger<ArchiveCopyService>.Instance);
        return new AttentionActions(
            store, archives, _factory, NullLogger<AttentionActions>.Instance);
    }

    private async Task SeedTrackAsync(
        int id, string path, string warning, int? durationMs = null)
    {
        await using var db = _factory.CreateDbContext();
        if (!await db.Playlists.AnyAsync(p => p.Id == "p1"))
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p1",
                Name = "Test",
                SourceUrl = "s:1",
            });
        }
        db.Tracks.Add(new TrackEntity
        {
            Id = id,
            MelodyId = $"m{id}",
            Title = "Test",
            Artist = "Artist",
            PlaylistEntityId = "p1",
            DownloadStatus = "downloaded",
            CurrentPath = path,
            DurationMs = durationMs,
            Warning = warning,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Reverify_ClearsTheUnverifiedWarning_WhenTheFileIsReadable()
    {
        var path = Path.Combine(_root, "track.mp3");
        await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3());
        await SeedTrackAsync(1, path, UnverifiedWarning);

        var result = await Actions(Store()).ReverifyAsync(new[] { 1 });

        Assert.That(result.Done, Is.EqualTo(1), string.Join("; ", result.Messages));
        using var check = _factory.CreateDbContext();
        var track = check.Tracks.Single(t => t.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(track.Warning, Is.Null.Or.Not.Contain("could not verify"),
                "the unverified state must not survive the repair");
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(
                AttentionClassifier.Classify(
                    track.DownloadStatus, track.DownloadError, track.ReviewKind, track.Warning),
                Is.Null,
                "the row leaves the unverified group instead of being reported as handled in place");
        });
    }

    [Test]
    public async Task Reverify_CorruptFile_KeepsTheBytesAndTheUnverifiedState()
    {
        var path = Path.Combine(_root, "broken.mp3");
        await File.WriteAllBytesAsync(path, new byte[128]);
        await SeedTrackAsync(1, path, $"quality hint: looks lossy; {UnverifiedWarning}");

        var result = await Actions(Store()).ReverifyAsync(new[] { 1 });

        using var check = _factory.CreateDbContext();
        var track = check.Tracks.Single(t => t.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(result.Done, Is.Zero, "a failed re-probe is not a handled track");
            Assert.That(File.Exists(path), Is.True,
                "a repair never deletes a file the user already has");
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.Warning, Does.Contain("could not verify the download"));
            Assert.That(track.Warning, Does.Contain("quality hint: looks lossy"),
                "the warning rewrite keeps facts that are not about verification");
            Assert.That(
                AttentionClassifier.Classify(
                    track.DownloadStatus, track.DownloadError, track.ReviewKind, track.Warning),
                Is.EqualTo(AttentionKinds.Unverified),
                "the row stays in the queue so the user sees the probe failure");
        });
    }

    [Test]
    public async Task Reverify_MissingFile_ReportsItAndLeavesTheRowAlone()
    {
        var path = Path.Combine(_root, "gone.mp3");
        await SeedTrackAsync(1, path, UnverifiedWarning);

        var result = await Actions(Store()).ReverifyAsync(new[] { 1 });

        using var check = _factory.CreateDbContext();
        var track = check.Tracks.Single(t => t.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(result.Done, Is.Zero);
            Assert.That(result.Messages.Single(), Does.Contain("download the track again"));
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.Warning, Is.EqualTo(UnverifiedWarning));
            Assert.That(
                AttentionClassifier.Classify(
                    track.DownloadStatus, track.DownloadError, track.ReviewKind, track.Warning),
                Is.EqualTo(AttentionKinds.Unverified));
        });
    }

    [Test]
    public async Task Reverify_LengthMismatch_BecomesAReviewWithoutDeletingTheFile()
    {
        var path = Path.Combine(_root, "short.mp3");
        await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3());
        // The tiny fixture is seconds long; ten minutes is outside the 30s window.
        await SeedTrackAsync(1, path, UnverifiedWarning, durationMs: 600_000);

        var result = await Actions(Store()).ReverifyAsync(new[] { 1 });

        using var check = _factory.CreateDbContext();
        var track = check.Tracks.Single(t => t.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(result.Done, Is.EqualTo(1), string.Join("; ", result.Messages));
            Assert.That(File.Exists(path), Is.True,
                "a length review keeps the playable file");
            Assert.That(TrackReview.Has(track.ReviewKind, TrackReview.Length), Is.True);
            Assert.That(
                AttentionClassifier.Classify(
                    track.DownloadStatus, track.DownloadError, track.ReviewKind, track.Warning),
                Does.StartWith("review-"));
        });
    }

    [Test]
    [NonParallelizable]
    public async Task Reverify_WithoutFfprobe_KeepsTheFileAndTheUnverifiedState()
    {
        var path = Path.Combine(_root, "track.mp3");
        await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3());
        await SeedTrackAsync(1, path, UnverifiedWarning);
        var action = Actions(Store());

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var empty = Path.Combine(_root, "empty-path");
        Directory.CreateDirectory(empty);
        Environment.SetEnvironmentVariable("PATH", empty);
        AttentionBulkResult result;
        try
        {
            result = await action.ReverifyAsync(new[] { 1 });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }

        using var check = _factory.CreateDbContext();
        var track = check.Tracks.Single(t => t.Id == 1);
        Assert.Multiple(() =>
        {
            Assert.That(result.Done, Is.Zero);
            Assert.That(result.Messages.Single(), Does.Contain("ffprobe"));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.Warning, Does.Contain("could not verify the download"));
            Assert.That(
                AttentionClassifier.Classify(
                    track.DownloadStatus, track.DownloadError, track.ReviewKind, track.Warning),
                Is.EqualTo(AttentionKinds.Unverified));
        });
    }
}
