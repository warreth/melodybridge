using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The dedup hard link judges a name that another writer may be using at
/// the very moment it links: the decision and the link run under the same
/// per-name lock the writers take. Without it, a pass that picked a free
/// name milliseconds before another writer took it links over the other
/// song's bytes.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class TrackLinkerConcurrencyTests
{
    private string _root = null!;
    private string _library = null!;
    private string _targetFolder = null!;
    private string _sourcePath = null!;
    private string _linkPath = null!;
    private TestSqliteFactory _factory = null!;
    private TrackLinker _linker = null!;
    private int _linkTrackId;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-linklock-{Guid.NewGuid():N}");
        _library = Path.Combine(_root, "library");
        _targetFolder = Path.Combine(_root, "targets");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_targetFolder);
        _sourcePath = Path.Combine(_library, "source-one.mp3");
        _linkPath = Path.Combine(_targetFolder, "source-one.mp3");
        File.WriteAllBytes(_sourcePath, TestAudio.MinimalMp3());
        _factory = new TestSqliteFactory(Path.Combine(_root, "links.db"));

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var sourcePlaylist = new PlaylistEntity
        {
            Id = "pl-link-src",
            Name = "Link Source",
            SourceUrl = "stub:one",
            TargetDirectory = _library,
        };
        sourcePlaylist.Tracks.Add(new TrackEntity
        {
            MelodyId = "mel-linklock",
            Title = "Source One",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = _sourcePath,
            MediaType = "mp3",
            Bitrate = 320,
        });
        var targetPlaylist = new PlaylistEntity
        {
            Id = "pl-link-target",
            Name = "Link Target",
            SourceUrl = "stub:two",
            TargetDirectory = _targetFolder,
            PreferredFormat = "mp3:320",
        };
        var linkRow = new TrackEntity
        {
            MelodyId = "mel-linklock",
            Title = "Source One",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "pending",
        };
        targetPlaylist.Tracks.Add(linkRow);
        db.Playlists.AddRange(sourcePlaylist, targetPlaylist);
        db.SaveChanges();
        _linkTrackId = linkRow.Id;

        _linker = new TrackLinker(
            _factory, new SettingsStore(_factory), new HardLinkService(),
            NullLogger<TrackLinker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A linking pass over its own context, off the caller thread.</summary>
    private async Task<TrackLinkOutcome> LinkAsync()
    {
        return await Task.Run(async () =>
        {
            await using var db = _factory.CreateDbContext();
            var track = await db.Tracks.FindAsync(_linkTrackId);
            var outcome = await _linker.TryLinkAsync(
                track!, db, PlaylistRules.ParseQuality("mp3:320"),
                pauseOnQualityMismatch: false, CancellationToken.None);
            if (outcome == TrackLinkOutcome.Linked) await db.SaveChangesAsync();
            return outcome;
        });
    }

    [Test]
    public async Task The_Link_Waits_For_The_Name_Its_Writer_Holds()
    {
        var scope = await PathLockTable.Shared.AcquireAsync(_linkPath);
        var link = LinkAsync();

        try
        {
            // The pass is inside the name lock by now: it cannot judge or
            // link while a writer holds the name.
            await Task.Delay(700);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(_linkPath), Is.False,
                    "a held name cannot have been linked yet");
                Assert.That(File.Exists(_sourcePath), Is.True,
                    "the candidate's file is untouched");
            });
        }
        finally
        {
            scope.Dispose();
        }

        var outcome = await link.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(TrackLinkOutcome.Linked));
            Assert.That(File.Exists(_linkPath), Is.True, "the link went when the name came free");
            Assert.That(File.Exists(_sourcePath), Is.True);
        });
        await using var check = _factory.CreateDbContext();
        var row = await check.Tracks.FindAsync(_linkTrackId);
        Assert.Multiple(() =>
        {
            Assert.That(row!.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(row.IsHardLink, Is.True);
        });
    }
}
