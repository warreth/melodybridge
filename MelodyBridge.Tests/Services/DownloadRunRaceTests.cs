using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// A download run loses a claim race and keeps working. When another
/// runner claims the row this run just selected, the conditional UPDATE
/// answers "zero rows", which used to stand for "nothing left to claim":
/// the run quit while other rows were still pending. The claimer reports
/// the two outcomes apart now, and the run looks again.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DownloadRunRaceTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    private sealed class FastPlugin : IDownloader
    {
        public string Id => "fast";
        public string Name => "Fast plugin";
        public string Description => "";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, "https://fast.example/1", TimeSpan.Zero));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            await File.WriteAllBytesAsync(path, TestAudio.MinimalMp3(), ct);
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "race.db"));
        await using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var playlist = new PlaylistEntity
        {
            Id = "pl-race",
            Name = "Race",
            SourceUrl = "stub:race",
            TargetDirectory = _root,
            PreferredFormat = "auto",
        };
        playlist.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-race-1", Title = "First", Artist = "Artist", Position = 0,
            DownloadStatus = "pending",
        });
        playlist.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-race-2", Title = "Second", Artist = "Artist", Position = 1,
            DownloadStatus = "pending",
        });
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task A_Runner_That_Lost_One_Claim_Keeps_Working()
    {
        var racy = new RacyClaimer(
            new DownloadPipeline.RowClaimer(_factory, new LiveTrackClaims()), _factory, _root);

        DownloadPipeline NewPipeline() => new(
            _factory,
            new DownloadManager(new ListDownloaderRegistry(new FastPlugin()),
                NullLogger<DownloadManager>.Instance),
            new SettingsStore(_factory),
            new TrackLinker(_factory, new SettingsStore(_factory), new HardLinkService(),
                NullLogger<TrackLinker>.Instance),
            null, null, null,
            NullLogger<DownloadPipeline>.Instance,
            () => new MelodyBridge.Infrastructure.Audio.VerificationOptions(),
            null, null, null,
            rowClaims: racy);

        var (downloaded, failed) = await NewPipeline().DownloadMissingAsync("pl-race");

        // The wrapper finishes the row the "other runner" won in the
        // background; give it its moment before judging the playlist.
        await racy.Settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var db = _factory.CreateDbContext();
        var rows = await db.Tracks.OrderBy(t => t.Position).ToListAsync();
        var rowState = string.Join(" | ", rows.Select(r =>
            $"{r.Title}: {r.DownloadStatus}{(r.DownloadError is null ? "" : $" [{r.DownloadError}]")}"));
        Assert.That(downloaded, Is.EqualTo(1),
            $"the run lost one row, downloaded the rest on later attempts; rows are: {rowState}");
        Assert.That(failed, Is.EqualTo(0), $"rows: {rowState}");
        Assert.Multiple(() =>
        {
            Assert.That(racy.LostRaces, Is.EqualTo(1), "the race happened exactly once");
            Assert.That(rows[0].DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(rows[1].DownloadStatus, Is.EqualTo("downloaded"));
        });
    }

    /// <summary>
    /// Stands in for the moment a second runner wins the row: the first
    /// real claim is answered with "lost to another runner", everything
    /// later goes through untouched. The claimed row is finished in the
    /// background like a real winner would, so the playlist ends whole.
    /// </summary>
    private sealed class RacyClaimer(
        DownloadPipeline.IRowClaimer inner, TestSqliteFactory factory, string rootPath)
        : DownloadPipeline.IRowClaimer
    {
        // The wrapper finishes the row it "wins" for real: the bytes land
        // in the fixture root like any other download would.
        private readonly string _root = rootPath;
        public int LostRaces { get; private set; }
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<(DownloadPipeline.ClaimedRow? Claim, bool LostRace)> ClaimNextPendingAsync(
            string playlistId, HashSet<int> exclude, CancellationToken ct)
        {
            var (claim, lostRace) = await inner.ClaimNextPendingAsync(playlistId, exclude, ct);
            if (claim is not null && LostRaces == 0)
            {
                LostRaces++;
                // This real claim is now the other runner's: they take the
                // row and finish it while this run looks elsewhere.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TakeClaimedRowAsync(claim.Id, exclude);
                        Settled.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        Settled.TrySetException(ex);
                    }
                });
                return (null, true);
            }
            return (claim, lostRace);
        }

        private async Task TakeClaimedRowAsync(int trackId, HashSet<int> exclude)
        {
            exclude.Remove(trackId);
            await using var db = await factory.CreateDbContextAsync();
            var track = await db.Tracks.FindAsync(trackId);
            if (track is null)
            {
                Settled.TrySetException(new InvalidOperationException("the won row vanished"));
                return;
            }
            var file = Path.Combine(_root, $"won-{trackId}.mp3");
            await File.WriteAllBytesAsync(file, TestAudio.MinimalMp3());
            track.DownloadStatus = "downloaded";
            track.CurrentPath = file;
            track.FileSizeBytes = new FileInfo(file).Length;
            await db.SaveChangesAsync();
        }
    }
}
