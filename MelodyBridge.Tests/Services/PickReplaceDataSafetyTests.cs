using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Picking a source for a track replaces the file the row already has.
/// The pick has honest ways to fail (the chosen plugin produces nothing,
/// the run is cancelled) and none of them may cost the user the only copy
/// they had: the old primary and archive copy are parked until a real
/// replacement commits, and come back when it does not.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class PickReplaceDataSafetyTests
{
    private string _root = null!;
    private string _music = null!;
    private string _archive = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-pick-replace-{Guid.NewGuid():N}");
        _music = Path.Combine(_root, "music");
        _archive = Path.Combine(_root, "archive");
        Directory.CreateDirectory(_music);
        Directory.CreateDirectory(_archive);
        _factory = new TestSqliteFactory(Path.Combine(_root, "pick-replace.db"));
        await using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class PickyPlugin : IDownloader
    {
        public string Id => "picky";
        public string Name => "Picky plugin";
        public string Description => "";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public enum Behavior
        {
            Succeed,
            ProduceNothing,
            WaitUntilCancelled,
        }

        public Behavior CurrentBehavior { get; set; } = Behavior.Succeed;
        public TaskCompletionSource DownloadStarted { get; set; } = new();

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            if (CurrentBehavior == Behavior.Succeed)
            {
                var good = Path.Combine(outputDirectory, $"{melodyId}.mp3");
                await File.WriteAllBytesAsync(good, TestAudio.MinimalMp3(), ct);
                return new DownloaderDownloadResult(true, good, null);
            }

            if (CurrentBehavior == Behavior.ProduceNothing)
                return new DownloaderDownloadResult(false, null, "no source had this track");

            // The third behavior stays inside DownloadAsync until the token
            // fires, then surfaces the cancellation the way the
            // well-behaved plugins do (Monochrome, SlSkd), so the pick
            // unwinds.
            DownloadStarted.TrySetResult();
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { /* the caller gave up */ }
            ct.ThrowIfCancellationRequested();
            return new DownloaderDownloadResult(false, null, "cancelled");
        }
    }

    private PlaylistStore NewStore(PickyPlugin plugin)
        => new(_factory, Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListDownloaderRegistry(plugin),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    /// <summary>One playlist, one track, a real primary and archive copy.</summary>
    private async Task<(PlaylistStore store, PickyPlugin plugin, TrackEntity track, PlaylistEntity playlist)>
        SeedAsync()
    {
        var plugin = new PickyPlugin();
        var store = NewStore(plugin);

        await using (var db = _factory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "music_path", Value = _music });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var playlist = new PlaylistEntity
            {
                Id = "pl-pick",
                Name = "Pick Replace",
                SourceUrl = "stub:pick",
                TargetDirectory = _music,
                ArchiveDirectory = _archive,
                PreferredFormat = "auto",
            };
            playlist.Tracks.Add(new TrackEntity
            {
                MelodyId = "mel-pick-1",
                Title = "The Song",
                Artist = "The Artist",
                Position = 0,
                DownloadStatus = "downloaded",
            });
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            var track = db.Tracks.Single();
            var primary = Path.Combine(_music, "old-primary.mp3");
            var archive = Path.Combine(_archive, "old-archive.mp3");
            await File.WriteAllBytesAsync(primary, TestAudio.MinimalMp3());
            await File.WriteAllBytesAsync(archive, TestAudio.MinimalMp3());
            track.CurrentPath = primary;
            track.ArchivePath = archive;
            track.FileSizeBytes = new FileInfo(primary).Length;
            await db.SaveChangesAsync();
            return (store, plugin, track, await db.Playlists.SingleAsync());
        }
    }

    private static TrackEntity FreshRow(TestSqliteFactory factory)
    {
        var db = factory.CreateDbContext();
        try { return db.Tracks.AsNoTracking().Single(); }
        finally { db.Dispose(); }
    }

    [Test]
    public async Task A_Failed_Pick_Keeps_The_Previous_File()
    {
        var (store, plugin, track, _) = await SeedAsync();

        plugin.CurrentBehavior = PickyPlugin.Behavior.ProduceNothing;
        var result = await store.DownloadCandidateAsync(track.Id, NewCandidate(), CancellationToken.None);

        Assert.That(result, Is.EqualTo("failed"));

        var row = FreshRow(_factory);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(track.CurrentPath!), Is.True, "the old primary survives a failed pick");
            Assert.That(File.Exists(track.ArchivePath!), Is.True, "the old archive copy survives too");
            Assert.That(row.CurrentPath, Is.EqualTo(track.CurrentPath), "the row still points at it");
            Assert.That(row.ArchivePath, Is.EqualTo(track.ArchivePath));
            Assert.That(row.DownloadStatus, Is.EqualTo("failed"));
            Assert.That(Directory.GetFiles(_root, "*.mb-previous-*", SearchOption.AllDirectories), Is.Empty,
                "no parked copy lingers after the pick is done");
        });
    }

    private static DownloadCandidate NewCandidate()
        => new("picky", "Picky plugin", "The Song", "The Artist", null, 320,
            "https://picky.example/1", MatchConfidence.High);

    [Test]
    public async Task A_Cancelled_Pick_Restores_What_The_Row_Had()
    {
        var (store, plugin, track, _) = await SeedAsync();

        plugin.CurrentBehavior = PickyPlugin.Behavior.WaitUntilCancelled;
        using var cts = new CancellationTokenSource();
        var pick = store.DownloadCandidateAsync(track.Id, NewCandidate(), cts.Token);
        await plugin.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await pick);

        var row = FreshRow(_factory);
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(track.CurrentPath!), Is.True, "the old primary survives the cancel");
            Assert.That(File.Exists(track.ArchivePath!), Is.True, "the old archive copy survives too");
            Assert.That(row.CurrentPath, Is.EqualTo(track.CurrentPath), "the row points back at it");
            Assert.That(row.ArchivePath, Is.EqualTo(track.ArchivePath));
            Assert.That(Directory.GetFiles(_root, "*.mb-previous-*", SearchOption.AllDirectories), Is.Empty,
                "no parked copy lingers after the pick is done");
        });
    }

    [Test]
    public async Task A_Successful_Pick_Replaces_And_Cleans_Up()
    {
        var (store, plugin, track, _) = await SeedAsync();

        plugin.CurrentBehavior = PickyPlugin.Behavior.Succeed;
        var result = await store.DownloadCandidateAsync(track.Id, NewCandidate(), CancellationToken.None);

        Assert.That(result, Is.EqualTo("downloaded"));

        var row = FreshRow(_factory);
        Assert.Multiple(() =>
        {
            Assert.That(row.CurrentPath, Is.Not.EqualTo(track.CurrentPath),
                "the row points at the fresh download");
            Assert.That(File.Exists(row.CurrentPath!), Is.True);
            Assert.That(File.Exists(row.ArchivePath!), Is.True, "the fresh archive copy is there");
            Assert.That(row.IsHardLink, Is.False, "fresh bytes are never a link");
            Assert.That(File.Exists(track.CurrentPath!), Is.False,
                "the replaced primary goes once the pick commits");
            Assert.That(File.Exists(track.ArchivePath!), Is.False, "with its archive copy");
            Assert.That(Directory.GetFiles(_root, "*.mb-previous-*", SearchOption.AllDirectories), Is.Empty,
                "no parked copy lingers after the pick is done");
        });
    }

    [Test]
    public async Task A_Shared_Name_Is_Not_Parked_Away_From_Its_Other_Row()
    {
        var (store, plugin, _, _) = await SeedAsync();
        var trackId = FreshRow(_factory).Id;
        var shared = Path.Combine(_music, "shared.mp3");
        var oldPrimary = FreshRow(_factory).CurrentPath!;
        var oldArchive = FreshRow(_factory).ArchivePath!;
        // A twin row in a second playlist points at the same primary: that
        // path belongs to both, the pick must not park it away from the
        // twin and the pick is refused because it cannot be replaced.
        await using (var db = _factory.CreateDbContext())
        {
            var second = new PlaylistEntity
            {
                Id = "pl-twin",
                Name = "Twin",
                SourceUrl = "stub:twin",
                TargetDirectory = _music,
            };
            db.Playlists.Add(second);
            second.Tracks.Add(new TrackEntity
            {
                MelodyId = "mel-twin-1",
                Title = "The Twin Song",
                Artist = "The Artist",
                Position = 0,
                DownloadStatus = "downloaded",
                CurrentPath = shared,
            });
            await db.SaveChangesAsync();
            File.Copy(oldPrimary, shared);
        }
        await using (var db = _factory.CreateDbContext())
        {
            var row = await db.Tracks.FindAsync(trackId);
            row!.CurrentPath = shared;
            row.ArchivePath = null;
            await db.SaveChangesAsync();
        }

        plugin.CurrentBehavior = PickyPlugin.Behavior.ProduceNothing;
        var result = await store.DownloadCandidateAsync(trackId, NewCandidate(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo("failed"));
            Assert.That(File.Exists(shared), Is.True, "the twin's file is untouched");
        });
    }
}
