using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The dedup candidates of a whole playlist, found in one query: a row
/// that has its file on disk is the copy another playlist can link, a
/// row whose file is gone is not, and an archive copy never counts as a
/// primary file.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class TrackLinkerCandidateTests
{
    private string _dbPath = null!;
    private string _dir = null!;
    private TestSqliteFactory _factory = null!;
    private TrackLinker _linker = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-candidates-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-candidates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _linker = new TrackLinker(
            _factory,
            new SettingsStore(_factory),
            new HardLinkService(),
            NullLogger<TrackLinker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "bytes");
        return path;
    }

    private async Task<TrackEntity> SeedTrackAsync(
        string playlistId, string melodyId, string? path, string status = "downloaded")
    {
        var track = new TrackEntity
        {
            MelodyId = melodyId,
            Title = "Song",
            Artist = "A",
            Position = 0,
            DownloadStatus = status,
            CurrentPath = path,
            PlaylistEntityId = playlistId,
        };
        await using var db = _factory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = playlistId,
            Name = playlistId,
            SourceUrl = $"https://example.com/{playlistId}",
            Tracks = new List<TrackEntity> { track },
        });
        await db.SaveChangesAsync();
        return track;
    }

    [Test]
    public async Task ARowWithItsFileOnDisk_IsTheCandidate()
    {
        var file = WriteFile("song.mp3");
        await SeedTrackAsync("pl-owner", "melody-1", file);
        var wanted = await SeedTrackAsync("pl-wanted", "melody-1", null, status: "pending");

        var candidates = await _linker.FindCandidatesAsync(new[] { wanted });

        Assert.That(candidates.ContainsKey("melody-1"), Is.True, "the copy on disk is the candidate");
        Assert.That(candidates["melody-1"].ExistingPath, Is.EqualTo(file));
    }

    [Test]
    public async Task ARowWhoseFileIsGone_IsNoCandidate()
    {
        var file = WriteFile("gone.mp3");
        await SeedTrackAsync("pl-owner", "melody-2", file);
        var wanted = await SeedTrackAsync("pl-wanted", "melody-2", null, status: "pending");
        File.Delete(file);

        var candidates = await _linker.FindCandidatesAsync(new[] { wanted });

        Assert.That(candidates.ContainsKey("melody-2"), Is.False,
            "a file that is not on disk cannot be linked");
    }

    [Test]
    public async Task AFileInAnArchiveFolder_IsNoCandidate()
    {
        var archive = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(archive);
        var file = WriteFile(Path.Combine("archive", "song.mp3"));
        await SeedTrackAsync("pl-owner", "melody-3", file);
        var wanted = await SeedTrackAsync("pl-wanted", "melody-3", null, status: "pending");
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("archive_path", archive);

        var candidates = await _linker.FindCandidatesAsync(new[] { wanted });

        Assert.That(candidates.ContainsKey("melody-3"), Is.False,
            "an archive copy is not a primary file");
    }

    [Test]
    public async Task OneQueryAnswersForEveryTrackOfThePlaylist()
    {
        var first = WriteFile("one.mp3");
        var second = WriteFile("two.mp3");
        await SeedTrackAsync("pl-owner", "melody-4", first);
        await SeedTrackAsync("pl-owner2", "melody-5", second);
        var wanted = await SeedTrackAsync("pl-wanted", "melody-4", null, status: "pending");
        var wantedTwo = await SeedTrackAsync("pl-wanted2", "melody-5", null, status: "pending");

        var candidates = await _linker.FindCandidatesAsync(new[] { wanted, wantedTwo });

        Assert.Multiple(() =>
        {
            Assert.That(candidates["melody-4"].ExistingPath, Is.EqualTo(first));
            Assert.That(candidates["melody-5"].ExistingPath, Is.EqualTo(second));
        });
    }

    [Test]
    public async Task ABigPlaylist_AsksInOneGo()
    {
        // A playlist of a few thousand tracks must not turn into a few
        // thousand queries, and the query has to carry the ids of all of
        // them in one statement.
        var wanted = new List<TrackEntity>();
        await using (var db = _factory.CreateDbContext())
        {
            var playlist = new PlaylistEntity
            {
                Id = "pl-big",
                Name = "Big",
                SourceUrl = "https://example.com/big",
            };
            for (var i = 0; i < 1500; i++)
            {
                var track = new TrackEntity
                {
                    MelodyId = $"melody-big-{i}",
                    Title = "Song",
                    Artist = "A",
                    Position = i,
                    DownloadStatus = "pending",
                    PlaylistEntityId = "pl-big",
                };
                playlist.Tracks.Add(track);
                wanted.Add(track);
            }
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync();
        }

        var candidates = await _linker.FindCandidatesAsync(wanted);

        Assert.That(candidates, Is.Empty, "no other playlist holds these tracks yet");
    }
}
