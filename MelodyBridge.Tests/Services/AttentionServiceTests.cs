using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The attention classifier is pure: exact statuses, errors and warnings
/// become stable groups, and a normal downloaded track is not noise.
/// </summary>
[TestFixture]
public class AttentionClassifierTests
{
    [TestCase("no source had this track", AttentionKinds.FailedNoSource)]
    [TestCase("No enabled plugin could download this link", AttentionKinds.FailedNoSource)]
    [TestCase("your quality filters excluded every plugin", AttentionKinds.FailedQualityFilter)]
    [TestCase("sources had 3 file(s) outside your quality filters", AttentionKinds.FailedQualityFilter)]
    [TestCase("sources only returned files in another format than the requested FLAC", AttentionKinds.FailedFormat)]
    [TestCase("sources only had versions with the wrong length", AttentionKinds.FailedDuration)]
    [TestCase("sources returned files that are not audio", AttentionKinds.FailedInvalidAudio)]
    [TestCase("corrupt download: the file is unreadable", AttentionKinds.FailedCorrupt)]
    [TestCase("Qobuz: The stored login no longer works", AttentionKinds.FailedAuth)]
    [TestCase("the track took longer than its 5 minute budget", AttentionKinds.FailedTimeout)]
    [TestCase("cancelled", AttentionKinds.FailedCancelled)]
    [TestCase("missing artist/title metadata", AttentionKinds.FailedMetadata)]
    [TestCase("something unexpected", AttentionKinds.FailedOther)]
    public void FailureMessages_MapToGroups(string error, string expected)
        => Assert.That(AttentionClassifier.ClassifyFailure(error), Is.EqualTo(expected));

    [Test]
    public void ReviewKinds_MapToReviewGroups()
    {
        Assert.That(AttentionClassifier.Classify("downloaded", null, "length", null),
            Is.EqualTo(AttentionKinds.ReviewLength));
        Assert.That(AttentionClassifier.Classify("downloaded", null, "spectrum", null),
            Is.EqualTo(AttentionKinds.ReviewSpectrum));
        Assert.That(AttentionClassifier.Classify("downloaded", null, "length+spectrum", null),
            Is.EqualTo(AttentionKinds.ReviewBoth));
    }

    [Test]
    public void ArchiveAndFileWarnings_AreAttention()
    {
        Assert.That(AttentionClassifier.Classify(
                "downloaded", null, null,
                "archive copy failed (Opus): ffmpeg not found on this host"),
            Is.EqualTo(AttentionKinds.Archive));
        Assert.That(AttentionClassifier.Classify(
                "downloaded", null, null,
                "file missing on disk, will re-download"),
            Is.EqualTo(AttentionKinds.FileMissing));
        Assert.That(AttentionClassifier.Classify(
                "downloaded", null, null,
                "could not verify the download: ffprobe is not installed"),
            Is.EqualTo(AttentionKinds.Unverified));
    }

    [Test]
    public void NormalDownload_AndInformationalWarnings_AreNotNoise()
    {
        Assert.That(AttentionClassifier.Classify("downloaded", null, null, null), Is.Null);
        Assert.That(AttentionClassifier.Classify(
                "downloaded", null, null, "quality hint: the spectrum looks good"), Is.Null);
        Assert.That(AttentionClassifier.Classify(
                "downloaded", null, null, "hardlinked from p1: FLAC"), Is.Null);
    }
}

/// <summary>
/// The attention query against a real SQLite database: one row per issue,
/// grouped, with playlist-level sync problems in their own list.
/// </summary>
[TestFixture]
public class AttentionServiceTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private AttentionService _service = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-attention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "state.db"));
        _service = new AttentionService(_factory);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Playlists.AddRange(
            new PlaylistEntity { Id = "p1", Name = "Mix", SourceUrl = "s:1" },
            new PlaylistEntity
            {
                Id = "p2",
                Name = "Partial",
                SourceUrl = "s:2",
                LastSyncStatus = SyncStatus.CompletedWithWarning,
                SyncWarning = "partial fetch: got 5 of 8 tracks",
            });
        db.Tracks.AddRange(
            Track("no-source", 1, "p1", "failed", "no source had this track"),
            Track("format", 2, "p1", "failed",
                "sources only returned files in another format than the requested FLAC"),
            Track("auth", 3, "p1", "failed", "Qobuz: The stored login no longer works"),
            Track("dedup", 4, "p1", "needs-review",
                warning: "dedup conflict: playlist wants FLAC, existing file is MP3"),
            Track("spectrum", 5, "p1", "downloaded", reviewKind: "spectrum",
                reviewText: "bandwidth stops at 16 kHz"),
            Track("archive", 6, "p1", "downloaded",
                warning: "archive copy failed (Opus): ffmpeg not found on this host"),
            Track("normal", 7, "p1", "downloaded"));
        await db.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static TrackEntity Track(
        string title, int id, string playlist, string status,
        string? error = null, string? reviewKind = null, string? reviewText = null,
        string? warning = null)
        => new()
        {
            Id = id,
            Title = title,
            Artist = "Artist",
            MelodyId = "mel-" + id,
            PlaylistEntityId = playlist,
            DownloadStatus = status,
            DownloadError = error,
            ReviewKind = reviewKind,
            ReviewText = reviewText,
            Warning = warning,
        };

    [Test]
    public async Task Snapshot_GroupsEveryKind_AndSkipsTheNormalTrack()
    {
        var snapshot = await _service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Groups.Select(group => group.Kind), Does.Contain(AttentionKinds.FailedNoSource));
            Assert.That(snapshot.Groups.Select(group => group.Kind), Does.Contain(AttentionKinds.FailedFormat));
            Assert.That(snapshot.Groups.Select(group => group.Kind), Does.Contain(AttentionKinds.FailedAuth));
            Assert.That(snapshot.Groups.Select(group => group.Kind), Does.Contain(AttentionKinds.Dedup));
            Assert.That(snapshot.Groups.Select(group => group.Kind), Does.Contain(AttentionKinds.ReviewSpectrum));
            Assert.That(snapshot.Groups.Select(group => group.Kind), Does.Contain(AttentionKinds.Archive));
            Assert.That(snapshot.TrackCount, Is.EqualTo(6));
            Assert.That(snapshot.Playlists, Has.Count.EqualTo(1));
            Assert.That(snapshot.Playlists[0].Name, Is.EqualTo("Partial"));
            Assert.That(snapshot.Playlists[0].SourceUrl, Is.EqualTo("s:2"));
            Assert.That(snapshot.Total, Is.EqualTo(7));
        });
        Assert.That(snapshot.Groups.SelectMany(group => group.Tracks).Any(track => track.Title == "normal"),
            Is.False, "a clean download is not attention");
    }

    [Test]
    public async Task Snapshot_CountsTheDashboardSlices()
    {
        var snapshot = await _service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FailedCount, Is.EqualTo(3));
            Assert.That(snapshot.DedupCount, Is.EqualTo(1));
            Assert.That(snapshot.ReviewCount, Is.EqualTo(1));
            Assert.That(snapshot.ArchiveCount, Is.EqualTo(1));
        });
    }
}
