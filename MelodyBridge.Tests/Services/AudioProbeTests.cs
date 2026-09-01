using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// AudioProbe and SchemaPatcher against real files and a real SQLite
/// database: sample rate and file size must come from the actual file,
/// and an old (pre-column) database must be upgraded in place.
/// </summary>
[TestFixture]
public class AudioProbeTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            return p is not null && p.WaitForExit(5000);
        }
        catch { return false; }
    }

    private static string RunFfmpeg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return p.ExitCode == 0 ? string.Empty : $"ffmpeg exited {p.ExitCode}";
    }

    [Test]
    public void Fill_MissingFile_OnlySizeStaysNullAndNothingThrows()
    {
        var track = new TrackEntity { MediaType = "mp3" };
        AudioProbe.Fill(track, Path.Combine(_dir, "gone.mp3"));
        Assert.That(track.FileSizeBytes, Is.Null);
        Assert.That(track.SampleRateHz, Is.Null);
    }

    [Test]
    public void Fill_RealFlac_ReadsSampleRateSizeAndContainer()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var path = Path.Combine(_dir, "song.flac");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=white:duration=2:sample_rate=48000\" \"{path}\""), Is.Empty);

        var track = new TrackEntity();
        AudioProbe.Fill(track, path);

        Assert.That(track.SampleRateHz, Is.EqualTo(48000), "sample rate must come from the real file");
        Assert.That(track.FileSizeBytes, Is.GreaterThan(0));
        Assert.That(track.MediaType, Is.EqualTo("flac"));
    }

    [Test]
    public async Task SchemaPatcher_OldDatabase_GainsColumnsWithoutDataLoss()
    {
        var dbPath = Path.Combine(_dir, "old.db");

        // Build a database the way an old release had it: Tracks without the
        // new columns, plus one row that must survive the upgrade.
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "keep-me",
                Title = "Survivor",
                CurrentPath = "/x/survivor.mp3",
                DownloadStatus = "downloaded",
            });
            await db.SaveChangesAsync();

            // Simulate the old schema by dropping the new columns.
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE Tracks DROP COLUMN SampleRateHz";
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE Tracks DROP COLUMN FileSizeBytes";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // The patcher must restore the columns and keep the row.
        await using (var db = factory.CreateDbContext())
        {
            await SchemaPatcher.PatchAsync(db);
        }

        await using (var db = factory.CreateDbContext())
        {
            var survivor = db.Tracks.Single(t => t.MelodyId == "keep-me");
            Assert.That(survivor.Title, Is.EqualTo("Survivor"));
            Assert.That(survivor.SampleRateHz, Is.Null, "added column starts empty");
        }

        // Idempotent: a second run must not fail.
        await using (var db = factory.CreateDbContext())
        {
            await SchemaPatcher.PatchAsync(db);
            Assert.Pass("patched twice without error");
        }
    }
}
