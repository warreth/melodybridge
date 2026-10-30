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
/// The add/refresh adoption path against a real SQLite database and real
/// tagged audio files: a playlist that was removed and added again must
/// reclaim the files still sitting in its folder instead of downloading
/// duplicates next to them. Covers the deterministic id, the old
/// "mb-" ids, untagged files, ambiguity, ownership and a quality
/// mismatch.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class MelodyFileAdoptionTests
{
    private string _dbPath = null!;
    private string _root = null!;
    private string _folder = null!;
    private TestSqliteFactory _factory = null!;
    private TrackLinker _linker = null!;

    [SetUp]
    public void SetUp()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-adopt-{id}.db");
        _root = Path.Combine(Path.GetTempPath(), $"mb-adopt-{id}");
        _folder = Path.Combine(_root, "Chill");
        Directory.CreateDirectory(_folder);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _linker = new TrackLinker(
            _factory, new SettingsStore(_factory), new HardLinkService(),
            NullLogger<TrackLinker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private static string WriteFlac(
        string folder, string fileName, string title, string artist,
        string? melodyId, int seconds = 3)
    {
        var path = Path.Combine(folder, fileName);
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

    private async Task SeedPlaylistAsync(
        string melodyId, string title = "Kids", string artist = "MGMT",
        int durationMs = 3000, string preferredFormat = "flac")
    {
        await using var db = _factory.CreateDbContext();
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-chill",
            Name = "Chill",
            SourceUrl = "https://open.spotify.com/playlist/chill",
            TargetDirectory = _folder,
            PreferredFormat = preferredFormat,
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = melodyId,
                    Title = title,
                    Artist = artist,
                    DurationMs = durationMs,
                    Position = 0,
                    DownloadStatus = "pending",
                    PlaylistEntityId = "pl-chill",
                },
            },
        });
        await db.SaveChangesAsync();
    }

    private async Task<TrackEntity> ReloadTrackAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tracks.AsNoTracking()
            .SingleAsync(t => t.PlaylistEntityId == "pl-chill");
    }

    [Test]
    public async Task AdoptsAFileWithTheDeterministicId()
    {
        var file = WriteFlac(_folder, "MGMT - Kids.flac", "Kids", "MGMT", "spotify:kids");
        await SeedPlaylistAsync("spotify:kids");

        await _linker.LinkSharedTracksAsync("pl-chill");

        var track = await ReloadTrackAsync();
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.CurrentPath, Is.EqualTo(file));
            Assert.That(File.Exists(file), Is.True);
            Assert.That(Directory.EnumerateFiles(_folder, "* (2).flac").Any(), Is.False,
                "the existing file must be adopted, not duplicated");
        });
    }

    [Test]
    public async Task AdoptsALegacyMbTagAndRewritesIt()
    {
        var file = WriteFlac(_folder, "MGMT - Kids.flac", "Kids", "MGMT", "mb-1a2b3c");
        await SeedPlaylistAsync("spotify:kids");

        await _linker.LinkSharedTracksAsync("pl-chill");

        var track = await ReloadTrackAsync();
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.CurrentPath, Is.EqualTo(file));
            Assert.That(TaglibHelper.ReadMelodyId(file), Is.EqualTo("spotify:kids"),
                "the old random id is replaced by the deterministic one");
        });
    }

    [Test]
    public async Task AdoptsAnUntaggedFileByMetadata()
    {
        var file = WriteFlac(_folder, "MGMT - Kids.flac", "Kids", "MGMT", melodyId: null);
        await SeedPlaylistAsync("spotify:kids");

        await _linker.LinkSharedTracksAsync("pl-chill");

        var track = await ReloadTrackAsync();
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.CurrentPath, Is.EqualTo(file));
            Assert.That(TaglibHelper.ReadMelodyId(file), Is.EqualTo("spotify:kids"));
        });
    }

    [Test]
    public async Task TwoMatchingFiles_AreAmbiguousAndLeftAlone()
    {
        var first = WriteFlac(_folder, "MGMT - Kids.flac", "Kids", "MGMT", "mb-first");
        var second = WriteFlac(_folder, "MGMT - Kids (2).flac", "Kids", "MGMT", "mb-second");
        await SeedPlaylistAsync("spotify:kids");

        await _linker.LinkSharedTracksAsync("pl-chill");

        var track = await ReloadTrackAsync();
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("pending"),
                "two equally good legacy candidates are never guessed");
            Assert.That(TaglibHelper.ReadMelodyId(first), Is.EqualTo("mb-first"));
            Assert.That(TaglibHelper.ReadMelodyId(second), Is.EqualTo("mb-second"));
        });
    }

    [Test]
    public async Task AFileOwnedByAnotherRow_IsNotRewritten()
    {
        var file = WriteFlac(_folder, "MGMT - Kids.flac", "Kids", "MGMT", "mb-owned");
        await SeedPlaylistAsync("spotify:kids");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-other",
                Name = "Other",
                SourceUrl = "https://open.spotify.com/playlist/other",
                TargetDirectory = Path.Combine(_root, "Other"),
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mb-owned",
                        Title = "Kids",
                        Artist = "MGMT",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        // The row owns the id but not this path, so the id
                        // check is what has to protect the file.
                        CurrentPath = Path.Combine(_root, "Other", "elsewhere.flac"),
                        PlaylistEntityId = "pl-other",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        await _linker.LinkSharedTracksAsync("pl-chill");

        var track = await ReloadTrackAsync();
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("pending"));
            Assert.That(TaglibHelper.ReadMelodyId(file), Is.EqualTo("mb-owned"),
                "another row's id must not be stolen");
        });
    }

    [Test]
    public async Task QualityMismatch_LeavesTheTrackForADownload()
    {
        // A real MP3 with the same tags; the playlist asks for FLAC.
        var file = Path.Combine(_folder, "MGMT - Kids.mp3");
        await File.WriteAllBytesAsync(file, TestAudio.MinimalMp3());
        TaglibHelper.WriteTags(file, title: "Kids", artist: "MGMT");
        TaglibHelper.WriteMelodyId(file, "spotify:kids");
        await SeedPlaylistAsync("spotify:kids", preferredFormat: "flac");

        await _linker.LinkSharedTracksAsync("pl-chill");

        var track = await ReloadTrackAsync();
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("pending"),
                "an MP3 is not a silent substitute for a FLAC request");
            Assert.That(track.DownloadStatus, Is.Not.EqualTo("needs-review"),
                "an eager pass does not write an unsolicited review");
        });
    }
}
