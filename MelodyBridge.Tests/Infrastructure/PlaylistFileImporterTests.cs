using System.Runtime.CompilerServices;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Parses the real file flavors a Spotify user can export (Exportify CSV,
/// privacy-export JSON) and pushes one through a real SQLite store: every
/// assertion reads back through a fresh DbContext, never the parsed object.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PlaylistFileImporterTests
{
    private const string ExportifyHeader =
        "Track URI,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms),Popularity,Explicit,Added By,Added At,Genres,Record Label,Danceability,Energy,Key,Loudness,Mode,Speechiness,Acousticness,Instrumentalness,Liveness,Valence,Tempo,Time Signature";

    private const string ExportifyRow1 =
        "spotify:track:2jpKZFnBk98Ud3EoZiBOTf,\"Here With Me\",\"Here With Me\",\"Marshmello;CHVRCHES\",2019-03-08,156313,77,false,,2026-09-01T11:04:20Z,\"edm,synthpop\",\"Joytime Collective\",0.792,0.566,5,-3.935,0,0.0438,0.0647,0,0.157,0.189,99.961,4";

    /// <summary>The sample row plus four more in the same shape.</summary>
    private static string ExportifyFiveRows => string.Join("\n",
        ExportifyHeader,
        ExportifyRow1,
        "spotify:track:2IZZqH4K02UIYg5EohpNHF,\"Zombie\",\"No Need To Argue\",\"The Cranberries\",1994-10-03,316520,77,false,,2026-09-01T11:05:20Z,\"alternative,rock\",\"Island Records\",0.653,0.941,9,-6.123,1,0.0407,0.00119,0,0.305,0.434,135.004,4",
        "spotify:track:0cgyeBU54kjmI54TflMANg,\"Neon Pill\",\"Neon Pill\",\"Cage The Elephant\",2024-01-19,191520,73,false,,2026-09-01T11:06:20Z,\"indie rock\",\"RCA\",0.571,0.864,1,-4.882,1,0.0566,0.0153,0,0.123,0.761,158.012,4",
        "spotify:track:4PTG3Z6ehGkBF3zI7Yg2qG,\"Take On Me\",\"Hunting High And Low\",\"a-ha\",1985-06-01,225120,82,false,,2026-09-01T11:07:20Z,\"synthpop,new wave\",\"Warner Bros.\",0.792,0.876,1,-9.457,0,0.0766,0.00259,0,0.101,0.751,168.884,4",
        "spotify:track:5ChkX8opmD0mU6X1ZgT9zE,\"Blinding Lights\",\"After Hours\",\"The Weeknd\",2020-02-07,200040,89,false,,2026-09-01T11:08:20Z,\"synthwave,pop\",\"XO\",0.514,0.931,0,-8.725,1,0.0524,0.00201,0,0.0933,0.704,171.005,4");

    // ── Exportify CSV ────────────────────────────────────────────────────

    [Test]
    public void ExportifyFullCsv_ParsesFiveTracks()
    {
        var parsed = PlaylistFileImporter.Parse("spotify.csv", ExportifyFiveRows);

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Kind, Is.EqualTo("exportify"));
        Assert.That(parsed.Playlists, Has.Count.EqualTo(1));
        Assert.That(parsed.Playlists[0].Tracks, Has.Count.EqualTo(5));

        var first = parsed.Playlists[0].Tracks[0];
        Assert.That(first.Title, Is.EqualTo("Here With Me"));
        Assert.That(first.Artist, Is.EqualTo("Marshmello, CHVRCHES"));
        Assert.That(first.Album, Is.EqualTo("Here With Me"));
        Assert.That(first.SongID, Is.EqualTo(new SongID(Platform.Spotify, "2jpKZFnBk98Ud3EoZiBOTf")));
        Assert.That(first.PlatformSongID!.ID, Is.EqualTo("2jpKZFnBk98Ud3EoZiBOTf"));
        Assert.That(first.Duration!.Value.TotalMilliseconds, Is.EqualTo(156313).Within(1));
        Assert.That(first.CurrentTrackLocation!.Path,
            Is.EqualTo("https://open.spotify.com/track/2jpKZFnBk98Ud3EoZiBOTf"));
        Assert.That(first.SourcePlatform, Is.EqualTo(Platform.Spotify));
    }

    [Test]
    public void ExportifyQuotedCell_CommaAndDoubledQuotesStayOneCell()
    {
        var csv = string.Join("\n",
            ExportifyHeader,
            "spotify:track:9Zfg8B2opmD0mU6X1ZgT9zF,\"Weird One\",\"Hits, Vol. \"\"2\"\"\",\"Test Artist\",2020-01-01,180000,50,false,,2026-09-01T11:04:20Z,\"rock\",\"Label\",0.5,0.5,0,-5,0,0.1,0.1,0,0.1,0.5,120,4");

        var parsed = PlaylistFileImporter.Parse("edge.csv", csv);

        Assert.That(parsed, Is.Not.Null);
        var tracks = parsed!.Playlists[0].Tracks;
        Assert.That(tracks, Has.Count.EqualTo(1));
        Assert.That(tracks[0].Album, Is.EqualTo("Hits, Vol. \"2\""));
        Assert.That(tracks[0].Title, Is.EqualTo("Weird One"));
    }

    [Test]
    public void ExportifySimpleMode_TracksHaveNoPlatformIds()
    {
        var csv = "Track Name,Artist Name(s),Album Name,Duration (ms)\n" +
                  "\"Simple Song\",\"Simple Artist\",\"Simple Album\",200000";

        var parsed = PlaylistFileImporter.Parse("simple.csv", csv);

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Kind, Is.EqualTo("exportify"));
        var track = parsed.Playlists[0].Tracks[0];
        Assert.That(track.SongID, Is.Null);
        Assert.That(track.PlatformSongID, Is.Null);
        Assert.That(track.Title, Is.EqualTo("Simple Song"));
        Assert.That(track.Artist, Is.EqualTo("Simple Artist"));
        Assert.That(track.Duration!.Value.TotalMilliseconds, Is.EqualTo(200000).Within(1));
    }

    [Test]
    public void ExportifyHeaderOnly_ReturnsNull()
    {
        Assert.That(PlaylistFileImporter.Parse("empty.csv", ExportifyHeader + "\n"), Is.Null);
    }

    // ── Spotify privacy export ───────────────────────────────────────────

    [Test]
    public void YourLibrary_ParsesLikedSongs()
    {
        const string json = "{ \"tracks\": [ { \"artist\": \"The Cranberries\", \"album\": \"No Need To Argue\", \"track\": \"Zombie\", \"uri\": \"spotify:track:2IZZqH4K02UIYg5EohpNHF\" } ] }";

        var parsed = PlaylistFileImporter.Parse("YourLibrary.json", json);

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Kind, Is.EqualTo("yourlibrary"));
        Assert.That(parsed.Playlists, Has.Count.EqualTo(1));
        var playlist = parsed.Playlists[0];
        Assert.That(playlist.Name, Is.EqualTo("Liked songs (Spotify)"));
        Assert.That(playlist.Tracks, Has.Count.EqualTo(1));

        var zombie = playlist.Tracks[0];
        Assert.That(zombie.Title, Is.EqualTo("Zombie"));
        Assert.That(zombie.Artist, Is.EqualTo("The Cranberries"));
        Assert.That(zombie.Album, Is.EqualTo("No Need To Argue"));
        Assert.That(zombie.SongID, Is.EqualTo(new SongID(Platform.Spotify, "2IZZqH4K02UIYg5EohpNHF")));
        Assert.That(zombie.IsLiked, Is.True);
        Assert.That(zombie.Duration, Is.Null);
    }

    [Test]
    public void Playlist1_ParsesMusicAndSkipsEpisodes()
    {
        const string json = "{ \"playlists\": [ { \"name\": \"RR\", \"lastModifiedDate\": \"2025-07-31\", \"items\": [ " +
            "{ \"track\": { \"trackName\": \"Neon Pill\", \"artistName\": \"Cage The Elephant\", \"albumName\": \"Neon Pill\", \"trackUri\": \"spotify:track:0cgyeBU54kjmI54TflMANg\" }, \"episode\": null, \"audiobook\": null, \"localTrack\": null, \"addedDate\": \"2024-08-17\" }, " +
            "{ \"track\": null, \"episode\": { \"episodeName\": \"Deep Dive\", \"podcastName\": \"Tech Talk\" }, \"audiobook\": null, \"localTrack\": null, \"addedDate\": \"2024-08-18\" } ] } ] }";

        var parsed = PlaylistFileImporter.Parse("Playlist1.json", json);

        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Kind, Is.EqualTo("playlists1"));
        Assert.That(parsed.Playlists, Has.Count.EqualTo(1));
        var playlist = parsed.Playlists[0];
        Assert.That(playlist.Name, Is.EqualTo("RR"));
        Assert.That(playlist.Tracks, Has.Count.EqualTo(1), "the episode row must be skipped");

        var track = playlist.Tracks[0];
        Assert.That(track.Title, Is.EqualTo("Neon Pill"));
        Assert.That(track.Artist, Is.EqualTo("Cage The Elephant"));
        Assert.That(track.SongID, Is.EqualTo(new SongID(Platform.Spotify, "0cgyeBU54kjmI54TflMANg")));
        Assert.That(track.IsLiked, Is.False);
    }

    [Test]
    public void GarbageContent_ReturnsNull()
    {
        Assert.That(PlaylistFileImporter.Parse("x.txt", "this is not a playlist file"), Is.Null);
        Assert.That(PlaylistFileImporter.Parse("x.txt", "{ broken json \"tracks\""), Is.Null);
        Assert.That(PlaylistFileImporter.Parse("x.txt", "col1,col2\n1,2"), Is.Null);
        Assert.That(PlaylistFileImporter.Parse("x.txt", ""), Is.Null);
    }

    // ── store round-trip on a real SQLite file ───────────────────────────

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-importer-{test}-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactoryAsync(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static PlaylistStore NewStore(IDbContextFactory<MelodyBridgeDbContext> factory)
        => new(factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    [Test]
    public async Task ImportFileAsync_PersistsAndReImportUpdates()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactoryAsync(dbPath);
            var store = NewStore(factory);

            var parsed = PlaylistFileImporter.Parse("spotify.csv", ExportifyFiveRows);
            Assert.That(parsed, Is.Not.Null);
            var result = await store.ImportFileAsync(parsed!);
            Assert.That(result.playlists, Is.EqualTo(1));
            Assert.That(result.tracks, Is.EqualTo(5));

            // Read back through a fresh context: real persistence, not the
            // entity we just held.
            await using (var db = factory.CreateDbContext())
            {
                var saved = await db.Playlists
                    .Include(p => p.Tracks)
                    .SingleAsync(p => p.SourceUrl.StartsWith("spotify:import:"));
                Assert.That(saved.SourceUrl, Does.StartWith("spotify:import:"));
                Assert.That(saved.SourcePlatform, Is.EqualTo(Platform.Spotify));
                Assert.That(saved.Tracks, Has.Count.EqualTo(5));
                Assert.That(saved.Tracks.Select(t => t.DownloadStatus), Is.All.EqualTo("pending"));
                Assert.That(saved.Tracks.Select(t => t.ExternalId),
                    Has.Member("2jpKZFnBk98Ud3EoZiBOTf"));
            }

            // Re-import of the same file must refresh, never duplicate.
            await store.ImportFileAsync(parsed!);
            await using (var db = factory.CreateDbContext())
            {
                var playlists = await db.Playlists
                    .Include(p => p.Tracks)
                    .Where(p => p.SourceUrl.StartsWith("spotify:import:"))
                    .ToListAsync();
                Assert.That(playlists, Has.Count.EqualTo(1), "re-import updates, never duplicates");
                Assert.That(playlists[0].Tracks, Has.Count.EqualTo(5), "re-import adds no track dupes");
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    /// <summary>No downloaders: the store must survive without any plugin.</summary>
    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
        public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
