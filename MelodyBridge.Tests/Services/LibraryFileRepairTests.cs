using System.Diagnostics;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The library repair pass against real tagged FLAC files: a downloaded
/// row whose old copy still sits under the plain name is normalized to the
/// deterministic id, then reported as a duplicate, and only the orphan is
/// removed. A group without an owning row is never offered for removal.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class LibraryFileRepairTests
{
    private string _dbPath = null!;
    private string _root = null!;
    private string _folder = null!;
    private TestSqliteFactory _factory = null!;
    private LibraryFileRepair _repair = null!;

    [SetUp]
    public void SetUp()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-repair-{id}.db");
        _root = Path.Combine(Path.GetTempPath(), $"mb-repair-{id}");
        _folder = Path.Combine(_root, "Chill");
        Directory.CreateDirectory(_folder);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var settings = new SettingsStore(_factory);
        var linker = new TrackLinker(
            _factory, settings, new HardLinkService(), NullLogger<TrackLinker>.Instance);
        _repair = new LibraryFileRepair(
            _factory, settings, linker, NullLogger<LibraryFileRepair>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private static string WriteFlac(
        string path, int seconds, string title, string artist, string? melodyId)
    {
        var ffmpeg = SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) Assert.Ignore("ffmpeg is required for a real FLAC fixture");
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-y", "-v", "error", "-f", "lavfi", "-i",
                     $"sine=frequency=440:duration={seconds}", "-c:a", "flac", path,
                 })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit(30000);
        Assert.That(process.ExitCode, Is.Zero, "the FLAC fixture did not encode");
        TaglibHelper.WriteTags(path, title: title, artist: artist);
        if (melodyId is not null) TaglibHelper.WriteMelodyId(path, melodyId);
        return path;
    }

    private async Task SeedDownloadedAsync(string path)
    {
        await using var db = _factory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-chill",
            Name = "Chill",
            SourceUrl = "https://open.spotify.com/playlist/chill",
            TargetDirectory = _folder,
            PreferredFormat = "flac",
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "spotify:kids",
                    Title = "Kids",
                    Artist = "MGMT",
                    DurationMs = 3000,
                    Position = 0,
                    DownloadStatus = "downloaded",
                    CurrentPath = path,
                    PlaylistEntityId = "pl-chill",
                },
            },
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task RepairNormalizesTheOldTagAndReportsTheOrphan()
    {
        var current = WriteFlac(
            Path.Combine(_folder, "MGMT - Kids (2).flac"), 4, "Kids", "MGMT", "spotify:kids");
        var old = WriteFlac(
            Path.Combine(_folder, "MGMT - Kids.flac"), 3, "Kids", "MGMT", "mb-old");
        await SeedDownloadedAsync(current);

        var report = await _repair.FindDuplicatesAsync();
        Assert.That(report.Groups, Is.Empty, "before repair the old file has another id");

        var repaired = await _repair.RepairAsync();
        Assert.That(repaired.TagsRewritten, Is.EqualTo(1));
        Assert.That(TaglibHelper.ReadMelodyId(old), Is.EqualTo("spotify:kids"));

        var duplicates = await _repair.FindDuplicatesAsync();
        var group = duplicates.Groups.Single();
        Assert.Multiple(() =>
        {
            Assert.That(group.Keeper, Is.EqualTo(current));
            Assert.That(group.Orphans, Is.EqualTo(new[] { old }));
            Assert.That(duplicates.CanRemove, Is.True);
        });

        var cleanup = await _repair.CleanDuplicatesAsync(new[] { old });
        Assert.Multiple(() =>
        {
            Assert.That(cleanup.Removed, Is.EqualTo(1));
            Assert.That(cleanup.Renamed, Is.EqualTo(1), "the keeper takes the plain name back");
            Assert.That(File.Exists(old), Is.True, "the keeper is now the plain file");
            Assert.That(File.Exists(current), Is.False, "the numbered duplicate is gone");
            Assert.That(TaglibHelper.ReadMelodyId(old), Is.EqualTo("spotify:kids"));
        });
        await using var verify = _factory.CreateDbContext();
        var row = verify.Tracks.Single();
        Assert.That(row.CurrentPath, Is.EqualTo(old));
    }

    [Test]
    public async Task TwoCopiesWithoutAnOwningRow_AreReportedButNotRemoved()
    {
        var first = WriteFlac(
            Path.Combine(_folder, "MGMT - Kids.flac"), 3, "Kids", "MGMT", "mb-old");
        var second = WriteFlac(
            Path.Combine(_folder, "MGMT - Kids (2).flac"), 3, "Kids", "MGMT", "mb-old");
        // No row at all: the group has no keeper.
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-chill",
                Name = "Chill",
                SourceUrl = "https://open.spotify.com/playlist/chill",
                TargetDirectory = _folder,
            });
            await db.SaveChangesAsync();
        }

        var duplicates = await _repair.FindDuplicatesAsync();
        Assert.That(duplicates.Groups, Has.Count.EqualTo(1));
        Assert.That(duplicates.Groups[0].Keeper, Is.Null);
        Assert.That(duplicates.CanRemove, Is.False, "nothing may be deleted without an owning file");

        var cleanup = await _repair.CleanDuplicatesAsync(new[] { first, second });
        Assert.That(cleanup.Removed, Is.Zero);
        Assert.That(File.Exists(first) && File.Exists(second), Is.True);
    }

    [Test]
    public async Task RemoveRefusesAPathThatIsNotInTheReport()
    {
        var current = WriteFlac(
            Path.Combine(_folder, "MGMT - Kids (2).flac"), 4, "Kids", "MGMT", "spotify:kids");
        var old = WriteFlac(
            Path.Combine(_folder, "MGMT - Kids.flac"), 3, "Kids", "MGMT", "mb-old");
        var unrelated = WriteFlac(
            Path.Combine(_folder, "Another - Song.flac"), 3, "Song", "Another", "spotify:other");
        await SeedDownloadedAsync(current);
        await _repair.RepairAsync();

        var cleanup = await _repair.CleanDuplicatesAsync(new[] { unrelated });

        Assert.That(cleanup.Removed, Is.Zero);
        Assert.That(File.Exists(unrelated), Is.True, "only paths the report names may be removed");
        Assert.That(File.Exists(old), Is.True);
    }
}
