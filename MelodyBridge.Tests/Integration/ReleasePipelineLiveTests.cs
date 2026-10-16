using System.Net.Http.Json;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The release gate's end-to-end run, one real workflow from end to end:
///
///   1. fetch a real public Spotify playlist into a real SQLite database;
///   2. download up to six of its tracks through the app's own waterfall,
///      booted from the real composition (every shipped plugin,
///      availability decides), until at least two real files exist;
///   3. check the quality facts against the files on disk;
///   4. check the opus archive copies the download produced;
///   5. write a real M3U with the real sync job runner and read it back;
///   6. push the playlist into a real Jellyfin container and stream the
///      audio bytes back through Jellyfin's own API;
///   7. do the same against a real Navidrome container over Subsonic;
///   8. start a real Plex container and check the honest report (a full
///      Plex push needs a claimed server and PLEX_TOKEN).
///
/// Nothing is stubbed. When a step fails, the release gate fails. The
/// download leg boots the app's real composition and runs the standard
/// waterfall: the gate never assembles a plugin list of its own, and a
/// source being down is the app's availability handling, not a gate
/// failure. Which of the first six tracks a live chart playlist has
/// indexed changes daily, so the gate demands two real files, not two
/// specific titles; every later step uses exactly the files that landed.
/// Tagged ReleaseGate only, so the expensive suite runs on
/// releases, not on every push. Requires network, yt-dlp and
/// ffmpeg/ffprobe on PATH and Docker with the jellyfin, navidrome and
/// plex images pulled.
/// </summary>
[TestFixture]
[Category("ReleaseGate")]
public class ReleasePipelineLiveTests
{
    /// <summary>Well-known public Spotify playlist ("Today's Top Hits").</summary>
    private const string PlaylistUrl = "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M";

    private const string JobName = "Release E2E";

    private string _root = null!;
    private string _musicDir = null!;
    private string _archiveDir = null!;
    private string _playlistDir = null!;
    private AppDownloadHost _app = null!;
    private IDbContextFactory<MelodyBridgeDbContext> _factory = null!;
    private PlaylistStore _store = null!;
    private string _playlistId = null!;
    private readonly Dictionary<int, long> _expectedDurations = new();

    private JellyfinLiveServer? _jellyfin;
    private NavidromeLiveServer? _navidrome;
    private PlexLiveServer? _plex;

    [OneTimeSetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-release-{Guid.NewGuid():N}");
        _musicDir = Path.Combine(_root, "music");
        _archiveDir = Path.Combine(_root, "archive");
        _playlistDir = Path.Combine(_root, "playlists");
        foreach (var dir in new[] { _musicDir, _archiveDir, _playlistDir })
            Directory.CreateDirectory(dir);

        // Boot the app exactly like the server does. The store, the
        // waterfall and the source providers all come from the real
        // composition; the gate owns no plugin list.
        _app = AppDownloadHost.Create();
        _factory = _app.DbFactory;
        _store = _app.Store;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _jellyfin?.Dispose();
        _navidrome?.Dispose();
        _plex?.Dispose();
        _app?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── 1: fetch ─────────────────────────────────────────────────────

    [Test, Order(1)]
    public async Task Fetch_RealSpotifyPlaylist_AndConfigureTheDownload()
    {
        var playlist = await _store.AddOrRefreshAsync(PlaylistUrl);
        _playlistId = playlist.Id;
        Assert.That(playlist.TrackCount, Is.GreaterThan(10),
            "the live Top Hits playlist must expose its tracks");

        // Configure the playlist the way the UI does: a target folder, the
        // standard High Quality preset (any lossy container up to 320 kbps)
        // and an opus archive copy.
        await _store.UpdateScheduleAsync(_playlistId, null, ScanSchedule.Manual,
            targetDirectory: _musicDir, preferredFormat: "preset:high",
            archiveDirectory: _archiveDir, archiveFormat: "opus");

        await using var db = await _factory.CreateDbContextAsync();
        var tracks = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == _playlistId)
            .OrderBy(t => t.Position)
            .ToListAsync();
        Assert.That(tracks, Has.Count.EqualTo(playlist.TrackCount),
            "every fetched track is persisted");
        Assert.That(tracks.All(t => !string.IsNullOrWhiteSpace(t.Title)
            && !string.IsNullOrWhiteSpace(t.Artist)), Is.True,
            "every track carries a title and an artist");
        Assert.That(tracks.All(t => !string.IsNullOrWhiteSpace(t.ExternalId)), Is.True,
            "every track keeps its Spotify id for the download lookup");
        Assert.That(tracks.All(t => t.DurationMs is > 0), Is.True,
            "the source durations are known before the download");
        foreach (var track in tracks)
            _expectedDurations[track.Id] = track.DurationMs!.Value;
    }

    // ── 2: download ──────────────────────────────────────────────────

    [Test, Order(2)]
    public async Task Download_Tracks_ThroughTheRealWaterfall()
    {
        // Six candidates, two required: which rows of a live chart playlist
        // are actually indexed by any source changes daily, while a source
        // outage fails every attempt and fails the gate either way.
        var (downloaded, failed) = await _store.DownloadMissingAsync(_playlistId, null, limit: 6);
        if (downloaded < 2)
        {
            // The app retries failed tracks on its next run; one more pass
            // is the same retry a user would trigger by hand.
            var (second, _) = await _store.DownloadMissingAsync(_playlistId, null, limit: 6);
            downloaded += second;
        }

        Assert.That(downloaded, Is.GreaterThanOrEqualTo(2),
            $"the waterfall must deliver at least two of the first six tracks " +
            $"(downloaded {downloaded}, failed in the first pass: {failed}); {FailureSummary()}");

        var rows = DownloadedTracks();
        Assert.That(rows, Has.Count.EqualTo(downloaded));
        foreach (var track in rows)
        {
            Assert.That(track.CurrentPath, Is.Not.Null.And.Not.Empty, "the file is recorded");
            Assert.That(File.Exists(track.CurrentPath!), Is.True,
                $"the downloaded file must exist: {track.CurrentPath}");
            Assert.That(new FileInfo(track.CurrentPath!).Length, Is.GreaterThan(100 * 1024),
                "a real audio file, not a stub");
            Assert.That(LossyExtensions,
                Does.Contain(Path.GetExtension(track.CurrentPath!).ToLowerInvariant()),
                "the High Quality preset delivers a real lossy container");
            Assert.That(TaglibHelper.ReadMelodyId(track.CurrentPath!), Is.EqualTo(track.MelodyId),
                "the file carries its MELODY_ID");
        }
    }

    // ── 3: quality ───────────────────────────────────────────────────

    [Test, Order(3)]
    public async Task Quality_FactsMatchTheFilesOnDisk()
    {
        var rows = DownloadedTracks();
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(2),
            "the quality facts need the downloaded files; " + FailureSummary());
        foreach (var track in rows)
        {
            Assert.That(track.DurationMs, Is.GreaterThan(30_000), "a real song, not a snippet");
            Assert.That(track.FileSizeBytes, Is.GreaterThan(100 * 1024));
            Assert.That(track.SampleRateHz, Is.GreaterThanOrEqualTo(44_100));
            Assert.That(track.Bitrate, Is.Null.Or.LessThanOrEqualTo(320),
                "the preset's 320 kbps ceiling holds on the real file");
            // High Quality promises "up to 320", but the waterfall takes
            // whatever a third-party source actually serves. The spectrum
            // check is advisory and content-dependent, so what a source
            // handed out shows up here honestly: a genuinely smaller rip
            // as a "quality hint", an upscaled upload the source itself
            // served as "bitrate looks inflated". Neither is a pipeline
            // failure. An operational one (a failed archive copy) still is.
            Assert.That(track.Warning ?? string.Empty, Does.Not.Contain("archive copy failed"),
                $"the archive copy of the delivered file failed: {track.Warning}");
            if (track.Warning is { Length: > 0 })
                Assert.That(track.Warning,
                    Does.StartWith("quality hint:").Or.StartWith("bitrate looks inflated:"),
                    $"unexpected quality warning on the delivered file: {track.Warning}");

            var integrity = FileIntegrity.Check(track.CurrentPath!,
                TimeSpan.FromMilliseconds(track.DurationMs!.Value));
            Assert.That(integrity.Ok, Is.True, integrity.Reason);

            // The probed duration must sit near what Spotify said.
            Assert.That(track.DurationMs!.Value, Is.EqualTo(_expectedDurations[track.Id]).Within(10_000),
                "the downloaded track matches the playlist's own duration");

            // ffprobe must agree on the container and see one audio stream.
            var codec = (await FfprobeAsync(
                "-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0", track.CurrentPath!)).Trim();
            Assert.That(new[] { "mp3", "aac", "opus" }, Does.Contain(codec),
                $"a real lossy container, not a stub or a lossless file ({codec})");
        }
    }

    // ── 4: archive copies ────────────────────────────────────────────

    [Test, Order(4)]
    public async Task Archive_OpusCopiesAreRealAudio()
    {
        var rows = DownloadedTracks();
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(2),
            "the archive copies belong to the downloaded files; " + FailureSummary());
        foreach (var track in rows)
        {
            Assert.That(track.ArchivePath, Is.Not.Null.And.Not.Empty,
                "the playlist's archive target must produce a copy");
            Assert.That(File.Exists(track.ArchivePath!), Is.True, track.ArchivePath);
            Assert.That(Path.GetExtension(track.ArchivePath!), Is.EqualTo(".opus"),
                "the archive format decides the extension");
            Assert.That(new FileInfo(track.ArchivePath!).Length, Is.GreaterThan(10 * 1024));

            var integrity = FileIntegrity.Check(track.ArchivePath!,
                TimeSpan.FromMilliseconds(track.DurationMs!.Value));
            Assert.That(integrity.Ok, Is.True, integrity.Reason);

            var codec = await FfprobeAsync(
                "-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0", track.ArchivePath!);
            Assert.That(codec.Trim(), Is.EqualTo("opus"), "a real opus transcode, not a renamed file");

            Assert.That(File.ReadAllBytes(track.ArchivePath!),
                Is.Not.EqualTo(File.ReadAllBytes(track.CurrentPath!)),
                "the archive is a transcode, not a byte copy");
        }
    }

    // ── 5: M3U ───────────────────────────────────────────────────────

    [Test, Order(5)]
    public async Task M3u_RealJobWritesTheDownloadedFiles()
    {
        var rows = DownloadedTracks();
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(2),
            "the M3U needs the downloaded files; " + FailureSummary());

        var m3uPath = Path.Combine(_playlistDir, "release-e2e.m3u");
        var runner = new SyncJobRunner(_factory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(),
            NullLogger<SyncJobRunner>.Instance);
        var job = new SyncJob
        {
            Id = "release-e2e-m3u",
            Name = JobName,
            SourceId = _playlistId,
            OutputTarget = OutputTargetType.M3uFile,
            M3uOutputPath = m3uPath,
        };

        var log = await runner.RunJobAsync(job);

        Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed), log.Message);
        Assert.That(log.ResolvedTracks, Is.EqualTo(rows.Count),
            "exactly the downloaded tracks are publishable; the rest has no file yet");

        var lines = await File.ReadAllLinesAsync(m3uPath);
        Assert.That(lines[0], Is.EqualTo("#EXTM3U"));
        var entries = lines.Where(l => l.Trim().Length > 0 && !l.StartsWith('#')).ToList();
        Assert.That(entries, Has.Count.EqualTo(rows.Count), "one entry per downloaded track");
        Assert.That(entries.All(File.Exists), Is.True,
            "every M3U entry points at a real file");
    }

    // ── 6: Jellyfin ──────────────────────────────────────────────────

    [Test, Order(6)]
    public async Task Jellyfin_Push_AndStreamTheRealFiles()
    {
        var rows = DownloadedTracks();
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(2),
            "Jellyfin needs the downloaded files; " + FailureSummary());

        _jellyfin = await JellyfinLiveServer.StartAsync(_musicDir, expectedTracks: rows.Count);
        foreach (var track in rows)
            await JellyfinLiveServer.WaitUntilResolvableAsync(_jellyfin.Api, track.Title!);

        // Sign in the way the wizard does: Jellyfin playlists belong to a
        // user and an API key has none, so the sync runs on a session token.
        // This is the flow users actually configure, not a hand-made token.
        var login = new JellyfinLoginService(
            new HttpClient(new HttpClientHandler()),
            NullLogger<JellyfinLoginService>.Instance);
        var signIn = await login.LoginAsync(
            JellyfinLiveServer.BaseUrl, JellyfinLiveServer.AdminUser, JellyfinLiveServer.AdminPw);
        Assert.That(signIn.Succeeded, Is.True, "Jellyfin sign-in failed: " + signIn.Error);

        var jellyfinSync = new JellyfinSync(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            NullLogger<JellyfinSync>.Instance, new JellyfinFixedSettings());
        var runner = new SyncJobRunner(_factory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            new IMediaServerSync[] { jellyfinSync },
            NullLogger<SyncJobRunner>.Instance);
        var job = new SyncJob
        {
            Id = "release-e2e-jellyfin",
            Name = JobName,
            SourceId = _playlistId,
            OutputTarget = OutputTargetType.JellyfinApi,
            JellyfinServerUrl = JellyfinLiveServer.BaseUrl,
            JellyfinApiKey = signIn.Token!,
            JellyfinUserId = signIn.UserId,
            PathRemapRules = new Dictionary<string, string> { [_musicDir] = "/media" },
        };

        var log = await runner.RunJobAsync(job);
        Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed), log.Message);
        Assert.That(jellyfinSync.LastReport!.ResolvedCount, Is.EqualTo(rows.Count),
            "the sync must resolve every downloaded file by its remapped path; unresolved: "
            + string.Join(", ", jellyfinSync.LastReport.UnresolvedPaths ?? Array.Empty<string>()));

        var mixes = await _jellyfin.PlaylistsNamedAsync(JobName);
        Assert.That(mixes, Has.Count.EqualTo(1), "the playlist exists on the real server");
        var playlistId = mixes[0].GetProperty("Id").GetString()!;
        var names = await _jellyfin.PlaylistItemNamesAsync(playlistId);
        Assert.That(names, Is.EquivalentTo(rows.Select(r => r.Title!).ToList()),
            "the server playlist holds the downloaded tracks");

        // Playable: Jellyfin streams the actual bytes of every entry.
        var items = await _jellyfin.Api.GetFromJsonAsync<JsonElement>(
            JellyfinLiveServer.BaseUrl + "/Playlists/" + playlistId + "/Items");
        var itemIds = items.GetProperty("Items").EnumerateArray()
            .Select(i => i.GetProperty("Id").GetString()!).ToList();
        Assert.That(itemIds, Has.Count.EqualTo(rows.Count));
        foreach (var itemId in itemIds)
        {
            var bytes = await _jellyfin.StreamFirstBytesAsync(itemId);
            Assert.That(bytes.Length, Is.GreaterThan(512),
                "Jellyfin served real audio bytes for the playlist entry");
            Assert.That(IsAudioStart(bytes), Is.True,
                "the streamed bytes are real audio, not an error page");
        }
    }

    // ── 7: Navidrome ─────────────────────────────────────────────────

    [Test, Order(7)]
    public async Task Navidrome_Push_AndStreamTheRealFiles()
    {
        var rows = DownloadedTracks();
        Assert.That(rows, Has.Count.GreaterThanOrEqualTo(2),
            "Navidrome needs the downloaded files; " + FailureSummary());

        _navidrome = await NavidromeLiveServer.StartAsync(_musicDir, expectedTracks: rows.Count);

        var runner = new SyncJobRunner(_factory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            new IMediaServerSync[]
            {
                new NavidromeSync(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
                    NullLogger<NavidromeSync>.Instance, new NavidromeFixedSettings()),
            },
            NullLogger<SyncJobRunner>.Instance);
        var job = new SyncJob
        {
            Id = "release-e2e-navidrome",
            Name = JobName,
            SourceId = _playlistId,
            OutputTarget = OutputTargetType.NavidromeApi,
            JellyfinServerUrl = NavidromeLiveServer.BaseUrl,
            JellyfinUserId = NavidromeLiveServer.Username,
            JellyfinApiKey = NavidromeLiveServer.Password,
        };

        var log = await runner.RunJobAsync(job);
        Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed), log.Message);

        var playlists = await _navidrome.GetSubsonicAsync("getPlaylists");
        var mix = playlists.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == JobName);
        Assert.That(mix.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined),
            "the playlist exists on the real server");
        var detail = await _navidrome.GetSubsonicAsync(
            "getPlaylist", "&id=" + mix.GetProperty("id").GetString());
        var songs = detail.GetProperty("playlist").GetProperty("entry").EnumerateArray().ToList();
        Assert.That(songs.Select(s => s.GetProperty("title").GetString()),
            Is.EquivalentTo(rows.Select(r => r.Title!).ToList()),
            "the server playlist holds the downloaded tracks");

        // Playable: Subsonic streams the actual bytes of every entry.
        using var http = new HttpClient();
        foreach (var song in songs)
        {
            using var resp = await http.GetAsync(
                _navidrome.SubsonicUrl("stream", "&id=" + song.GetProperty("id").GetString()));
            Assert.That(resp.IsSuccessStatusCode, Is.True, $"stream -> {(int)resp.StatusCode}");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.That(bytes.Length, Is.GreaterThan(512),
                "Navidrome served real audio bytes for the playlist entry");
            Assert.That(IsAudioStart(bytes), Is.True,
                "the streamed bytes are real audio, not an error page");
        }
    }

    // ── 8: Plex ──────────────────────────────────────────────────────

    [Test, Order(8)]
    public async Task Plex_RealContainer_ReportsTheMissingLibraryHonestly()
    {
        _plex = await PlexLiveServer.StartAsync();

        var identity = await _plex.Http.GetFromJsonAsync<JsonElement>(PlexLiveServer.BaseUrl + "/identity");
        Assert.That(identity.GetProperty("MediaContainer").TryGetProperty("machineIdentifier", out _), Is.True,
            "the real server answers with its machine id");

        // Full playlist sync needs a claimed server with a music library;
        // without PLEX_TOKEN the sync must explain that, not crash. The
        // deep test in PlexSyncLiveTests runs when a token is provided.
        var sync = new PlexSync(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<PlexSync>.Instance, new PlexFixedSettings());
        var playlist = new Playlist
        {
            Name = JobName,
            Tracks = DownloadedTracks().Select(t => new Track
            {
                Title = t.Title,
                CurrentTrackLocation = new FileLocation(t.CurrentPath!),
            }).ToList(),
        };
        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/ignored.m3u", false, null));

        Assert.That(sync.LastReport!.Message, Does.Contain("music"),
            "an unclaimed server without a music library explains itself: " + sync.LastReport.Message);
    }

    // ── helpers ──────────────────────────────────────────────────────

    /// <summary>Real lossy containers the presets may deliver.</summary>
    private static readonly string[] LossyExtensions = { ".mp3", ".m4a", ".aac", ".opus", ".ogg" };

    /// <summary>The downloaded tracks, read back through a fresh context.</summary>
    private List<TrackEntity> DownloadedTracks()
    {
        using var db = _factory.CreateDbContext();
        return db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == _playlistId && t.DownloadStatus == "downloaded")
            .OrderBy(t => t.Position)
            .ToList();
    }

    /// <summary>
    /// Why each failed track failed, read back from the database: the
    /// manager's own reason survives on the row (no source had it, wrong
    /// length, outside the filters), plus the app's own log tail so a
    /// failing run names the plugin-level cause too.
    /// </summary>
    private string FailureSummary()
    {
        using var db = _factory.CreateDbContext();
        var failures = db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == _playlistId && t.DownloadStatus == "failed")
            .OrderBy(t => t.Position)
            .Select(t => new { t.Artist, t.Title, t.DownloadError })
            .ToList();
        var reasons = failures.Count == 0
            ? "no failed rows recorded"
            : "failed tracks: " + string.Join(" | ",
                failures.Select(f => $"{f.Artist} - {f.Title}: {f.DownloadError ?? "no error recorded"}"));
        return reasons + " -- app log tail: " + _app.Logs.Tail(30);
    }

    /// <summary>
    /// Real audio starts: an ID3 tag, an MPEG frame sync/ADTS, OggS, fLaC,
    /// RIFF/WAVE or an ISO base media ("ftyp") header.
    /// </summary>
    private static bool IsAudioStart(byte[] bytes)
        => bytes.Length >= 4
            && ((bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)                     // "ID3"
                || (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)                              // MPEG/ADTS sync
                || (bytes[0] == 0x4F && bytes[1] == 0x67 && bytes[2] == 0x67 && bytes[3] == 0x53)  // "OggS"
                || (bytes[0] == 0x66 && bytes[1] == 0x4C && bytes[2] == 0x61 && bytes[3] == 0x43)  // "fLaC"
                || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)  // "RIFF"
                || (bytes.Length >= 8 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)); // "ftyp"

    private static async Task<string> FfprobeAsync(string arguments, string file)
    {
        using var ffprobe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"{arguments} \"{file}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = await ffprobe.StandardOutput.ReadToEndAsync();
        await ffprobe.WaitForExitAsync();
        Assert.That(ffprobe.ExitCode, Is.EqualTo(0), $"ffprobe rejected {file}");
        return stdout;
    }

    private sealed class JellyfinFixedSettings : IJellyfinSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(JellyfinLiveServer.BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult("");
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class NavidromeFixedSettings : INavidromeSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.Password);
    }

    private sealed class PlexFixedSettings : IPlexSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(PlexLiveServer.BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default)
            => Task.FromResult(Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? "unclaimed-token");
    }
}
