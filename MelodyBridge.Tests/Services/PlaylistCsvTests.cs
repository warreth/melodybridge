using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Csv.Escape quoting rules and ExportCsvAsync against a real SQLite file
/// database: exact header, bom, comma/quote round-trip, seeded values.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class PlaylistCsvTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-csv-{test}-{Guid.NewGuid():N}.db");

    private static async Task<PlaylistStore> NewStoreAsync(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using (var db = factory.CreateDbContext())
            await db.Database.EnsureCreatedAsync();
        return new PlaylistStore(
            factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);
    }

    private static async Task<string> SeedAsync(string dbPath, string playlistName)
    {
        var factory = await NewDbFactoryOnly(dbPath);
        await using var db = factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Id = "pl-csv",
            Name = playlistName,
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "m1",
                    Title = "Love, \"Honestly\"",
                    Artist = "Some, Artist",
                    Album = "Greatest Hits",
                    DurationMs = 215_000,
                    Position = 0,
                    DownloadStatus = "downloaded",
                    Bitrate = 320,
                    SampleRateHz = 44100,
                    MediaType = "mp3",
                    FileSizeBytes = 8_630_000,
                    CurrentPath = "/music/downloaded/love-honestly.mp3",
                },
                new()
                {
                    MelodyId = "m2",
                    Title = "Plain track",
                    Artist = "Solo",
                    Position = 1,
                    DownloadStatus = "pending",
                    // CurrentPath stays null: the filename cell must come out empty
                },
            },
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        return playlist.Id;
    }

    // separate factory for seeding: the store keeps its own factory over the same file
    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactoryOnly(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
    }

    [Test]
    public void Escape_PlainValue_Unquoted()
        => Assert.That(Csv.Escape("plain"), Is.EqualTo("plain"));

    [Test]
    public void Escape_Null_IsEmpty()
        => Assert.That(Csv.Escape(null), Is.EqualTo(string.Empty));

    [Test]
    public void Escape_Comma_IsQuoted()
        => Assert.That(Csv.Escape("a,b"), Is.EqualTo("\"a,b\""));

    [Test]
    public void Escape_Quote_IsDoubledAndQuoted()
        => Assert.That(Csv.Escape("say \"hi\""), Is.EqualTo("\"say \"\"hi\"\"\""));

    [Test]
    public void Escape_Newline_IsQuoted()
    {
        Assert.That(Csv.Escape("line1\nline2"), Is.EqualTo("\"line1\nline2\""));
        Assert.That(Csv.Escape("line1\r\nline2"), Is.EqualTo("\"line1\r\nline2\""));
    }

    [Test]
    public async Task ExportCsvAsync_ProducesBomHeaderAndRows()
    {
        var dbPath = NewDbPath();
        try
        {
            var store = await NewStoreAsync(dbPath);
            var id = await SeedAsync(dbPath, "Csv test");

            var bytes = await store.ExportCsvAsync(id);
            var text = Decode(bytes, out var body);

            // bom first so excel opens utf-8 correctly
            Assert.That(text.StartsWith("\uFEFF"), Is.True);

            var lines = body.Split('\n');
            Assert.That(lines[0].TrimEnd('\r'),
                Is.EqualTo("Position,Title,Artist,Album,DurationMs,Status,BitrateKbps,SampleRateHz,MediaType,FileSizeBytes,Filename"));

            // 1-based positions for humans, every seeded value lands in its cell
            Assert.That(lines[1].TrimEnd('\r'), Is.EqualTo(
                "1,\"Love, \"\"Honestly\"\"\",\"Some, Artist\",Greatest Hits,215000,downloaded,320,44100,mp3,8630000,love-honestly.mp3"));

            // pending row: empty quality cells and an empty filename cell
            Assert.That(lines[2].TrimEnd('\r'), Is.EqualTo(
                "2,Plain track,Solo,,,pending,,,,,"));

            // the comma/quote title round-trips through one split back to the original
            var cells = SplitCsvRow(lines[1].TrimEnd('\r'));
            Assert.That(cells[1], Is.EqualTo("Love, \"Honestly\""));
            Assert.That(cells[2], Is.EqualTo("Some, Artist"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task ExportCsvAsync_MissingPlaylist_Throws()
    {
        var dbPath = NewDbPath();
        try
        {
            var store = await NewStoreAsync(dbPath);
            Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ExportCsvAsync("no-such-playlist"));
        }
        finally { TryDelete(dbPath); }
    }

    private static string Decode(byte[] bytes, out string body)
    {
        var text = Encoding.UTF8.GetString(bytes);
        body = text.StartsWith("\uFEFF") ? text[1..] : text;
        return text;
    }

    /// <summary>minimal csv row splitter mirroring the quoting rules of Csv.Escape.</summary>
    private static List<string> SplitCsvRow(string line)
    {
        var cells = new List<string>();
        var i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { cells.Add(string.Empty); break; }
            if (line[i] == ',') { cells.Add(string.Empty); i++; continue; }
            if (line[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; }
                    }
                    else sb.Append(line[i++]);
                }
                cells.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                var end = line.IndexOf(',', i);
                if (end < 0) { cells.Add(line[i..]); break; }
                cells.Add(line[i..end]);
                i = end + 1;
            }
        }
        return cells;
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
