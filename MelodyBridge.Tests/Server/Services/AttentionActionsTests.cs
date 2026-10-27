using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The bulk attention actions are thin loops around the store. The loop,
/// the failure count and the progress contract are what a page relies on;
/// the store itself is covered by the download and dedup suites.
/// </summary>
[TestFixture]
public class AttentionActionsTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private RecordingStore _store = null!;
    private AttentionActions _actions = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-actions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "state.db"));
        _store = new RecordingStore(_factory);
        var archives = new ArchiveCopyService(
            _factory,
            new SettingsStore(_factory),
            NullLogger<ArchiveCopyService>.Instance);
        _actions = new AttentionActions(
            _store, archives, _factory,
            NullLogger<AttentionActions>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Retry_CountsSuccessesAndReportsTheRest()
    {
        _store.DownloadStatus = id => id == 1 ? "downloaded" : "failed";
        var progress = new List<int>();

        var result = await _actions.RetryAsync(
            new[] { 1, 2 }, avoidLastProvider: false, new Progress<int>(progress.Add));

        Assert.Multiple(() =>
        {
            Assert.That(result.Done, Is.EqualTo(1));
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Messages.Single(), Does.Contain("Track 2"));
            Assert.That(result.Summary, Does.Contain("1 handled"));
        });
    }

    [Test]
    public async Task RetryElsewhere_PassesTheAvoidFlag()
    {
        var result = await _actions.RetryAsync(new[] { 1 }, avoidLastProvider: true);

        Assert.That(result.Done, Is.EqualTo(1));
        Assert.That(_store.LastDownloadAvoid, Is.True);
    }

    [Test]
    public async Task Hardlink_ReportsTracksWithoutAMatch()
    {
        _store.LinkResult = id => id == 1;

        var result = await _actions.HardlinkAsync(new[] { 1, 2 });

        Assert.Multiple(() =>
        {
            Assert.That(result.Done, Is.EqualTo(1));
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Messages.Single(), Does.Contain("no matching file"));
        });
    }

    [Test]
    public async Task DedupHardlink_DoesNotDownload()
    {
        var result = await _actions.DedupAsync(new[] { 1 }, hardlinkExisting: true);

        Assert.That(result.Done, Is.EqualTo(1));
        Assert.That(_store.DownloadCalls, Is.Zero, "hardlinking never redownloads");
    }

    [Test]
    public async Task DedupDownload_ResolvesThenDownloads()
    {
        var result = await _actions.DedupAsync(new[] { 1 }, hardlinkExisting: false);

        Assert.That(result.Done, Is.EqualTo(1));
        Assert.That(_store.DownloadCalls, Is.EqualTo(1));
        Assert.That(_store.LastDownloadAvoid, Is.False);
    }

    private sealed class RecordingStore : PlaylistStore
    {
        public Func<int, string> DownloadStatus { get; set; } = _ => "downloaded";
        public Func<int, bool> LinkResult { get; set; } = _ => true;
        public Func<int, bool> ReviewResult { get; set; } = _ => true;
        public int DownloadCalls { get; private set; }
        public bool LastDownloadAvoid { get; private set; }

        public RecordingStore(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
            : base(dbFactory,
                Array.Empty<ISourceProvider>(),
                new Mock<IDownloadManager>().Object,
                NullLogger<PlaylistStore>.Instance)
        {
        }

        public override Task<string> DownloadTrackAsync(
            int trackId, CancellationToken ct = default, bool avoidLastProvider = false)
        {
            DownloadCalls++;
            LastDownloadAvoid = avoidLastProvider;
            return Task.FromResult(DownloadStatus(trackId));
        }

        public override Task<bool> LinkTrackAsync(int trackId, CancellationToken ct = default)
            => Task.FromResult(LinkResult(trackId));

        public override Task<bool> ResolveReviewAsync(
            int trackId, bool hardlinkExisting, CancellationToken ct = default)
            => Task.FromResult(ReviewResult(trackId));

        public override Task<bool> ResolveQualityReviewAsync(
            int trackId, QualityReviewAction action, CancellationToken ct = default)
            => Task.FromResult(ReviewResult(trackId));
    }

    [Test]
    public async Task QualityKeep_DoesNotRedownload()
    {
        var result = await _actions.QualityReviewAsync(
            new[] { 1 }, QualityReviewAction.Keep);

        Assert.That(result.Done, Is.EqualTo(1));
        Assert.That(_store.DownloadCalls, Is.Zero);
    }
}
