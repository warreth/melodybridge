using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One canonical pipeline from source URL to persisted playlist:
/// resolve provider → fetch live playlist → replace track snapshot in SQLite.
/// Track rows keep their platform IDs and ordering so later stages
/// (downloaders, library scans, sync jobs) can join on them.
/// </summary>
public class PlaylistStore
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ISourceProvider[] _providers;
    private readonly IAccountSourceProvider[] _accountProviders;
    private readonly IDownloadManager _downloadManager;
    private readonly ILogger<PlaylistStore> _logger;
    private readonly SettingsStore _settings;

    /// <summary>
    /// Post-download spectral verification strictness. Static: one app-wide
    /// setting that the Server sets at startup and the Settings page can
    /// flip without touching DI lifetimes.
    /// </summary>
    public static Func<SpectrumMode> SpectrumVerification { get; set; } = () => SpectrumMode.Fast;

    public PlaylistStore(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IEnumerable<ISourceProvider> providers,
        IDownloadManager downloadManager,
        ILogger<PlaylistStore> logger,
        IEnumerable<IAccountSourceProvider>? accountProviders = null,
        SettingsStore? settings = null)
    {
        _dbFactory = dbFactory;
        _providers = providers.ToArray();
        _accountProviders = accountProviders?.ToArray() ?? Array.Empty<IAccountSourceProvider>();
        _downloadManager = downloadManager;
        _logger = logger;
        // Settings live in the same database: the lazily built store reads
        // the same rows the injected singleton would.
        _settings = settings ?? new SettingsStore(dbFactory);
    }

    /// <summary>
    /// Download every track of a playlist that has no local file yet, into the
    /// playlist's TargetDirectory (or override). Updates DownloadStatus/
    /// CurrentPath per track as it goes. Returns (downloaded, failed).
    ///
    /// Tracks are claimed one at a time (atomically, status pending ->
    /// in_progress) so several concurrent callers never race on the same
    /// track: that is what makes the coordinator's max-concurrent
    /// workers safe.
    /// </summary>
    public async Task<(int downloaded, int failed)> DownloadMissingAsync(
        string playlistId, string? outputDirectoryOverride = null, int limit = int.MaxValue, CancellationToken ct = default)
    {
        // Prologue: read the run-wide settings (folder, format) once, then
        // let go of the context. Each track below runs in its own fresh
        // context so nothing stale survives between claims.
        string dir, playlistName, preferredFormat;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var entity = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == playlistId, ct)
                ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

            // Folder precedence: an explicit override, the playlist's own
            // folder, then the app-wide music_path default.
            dir = outputDirectoryOverride ?? entity.TargetDirectory
                ?? await _settings.GetAsync("music_path", "/music", ct);
            if (string.IsNullOrWhiteSpace(dir))
                throw new InvalidOperationException(
                    "No download folder configured. Set one in the playlist settings.");
            playlistName = entity.Name;
            preferredFormat = entity.PreferredFormat;
        }

        var downloaded = 0;
        var failed = 0;
        var spectrumMode = SpectrumVerification();
        // Tracks already attempted in this call: failed tracks stay
        // claimable (so later runs retry them), but never twice in one call.
        var attempted = new HashSet<int>();

        for (var attempt = 0; attempt < limit; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var trackId = await ClaimNextPendingAsync(playlistId, attempted, ct);
            if (trackId is null) break; // nothing left to claim

            var result = await DownloadClaimedTrackAsync(
                trackId.Value, dir, preferredFormat, spectrumMode, ct);
            if (result == "downloaded") downloaded++;
            else if (result == "failed") failed++;
        }

        _logger.LogInformation("Download run for '{Playlist}' ({Quality}): {Downloaded} downloaded, {Failed} failed",
            playlistName, preferredFormat, downloaded, failed);

        return (downloaded, failed);
    }

    /// <summary>
    /// Downloads exactly one track (the per-track button). Claims that
    /// track atomically, runs the same pipeline as a full run, and
    /// returns "downloaded", "failed" or "claimed-by-another-run",
    /// never touching any other track of the playlist.
    /// </summary>
    public async Task<string> DownloadTrackAsync(int trackId, CancellationToken ct = default)
    {
        int id;
        string dir, preferredFormat;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var track = await db.Tracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == trackId, ct);
            if (track is null) return "not-found";
            id = track.Id;

            var playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
            dir = playlist?.TargetDirectory
                ?? await _settings.GetAsync("music_path", "/music", ct);
            preferredFormat = playlist?.PreferredFormat ?? "";
        }
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException(
                "No download folder configured. Set one in the playlist settings.");

        // Claim exactly this track: same conditional UPDATE as the
        // full-run claim, so a single-track click and a running batch
        // never download the same track twice.
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var claimed = await db.Tracks
                .Where(t => t.Id == id
                    && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct);
            if (claimed != 1) return "claimed-by-another-run";
        }

        return await DownloadClaimedTrackAsync(id, dir, preferredFormat, SpectrumVerification(), ct);
    }

    /// <summary>
    /// The per-track pipeline shared by the full run and the single-track
    /// button: search, download, tag, integrity-check, write status.
    /// The caller has already claimed the track.
    /// </summary>
    private async Task<string> DownloadClaimedTrackAsync(
        int trackId, string dir, string preferredFormat, SpectrumMode spectrumMode, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.FindAsync(new object[] { trackId }, ct);
        if (track is null) return "skipped";

        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
        {
            track.DownloadStatus = "failed";
            track.DownloadError = "missing artist/title metadata";
            await db.SaveChangesAsync(ct);
            return "failed";
        }

        try
        {
            var (primary, fallback) = ParseQualityDetailed(preferredFormat);
            var path = await _downloadManager.DownloadTrackAsync(
                track.Artist!, track.Title!, dir, track.MelodyId!, primary, ct);

            // Lossless preset: when no source delivered a lossless
            // file, take the best lossy copy instead of nothing.
            if (path is null && fallback is not null)
            {
                _logger.LogInformation(
                    "No lossless source for '{Title}'; falling back to the best lossy file", track.Title);
                path = await _downloadManager.DownloadTrackAsync(
                    track.Artist!, track.Title!, dir, track.MelodyId!, fallback, ct);
            }

            if (path is null)
            {
                track.DownloadStatus = "failed";
                // Say why: filter misses can be fixed by the user,
                // missing tracks cannot.
                track.DownloadError = _downloadManager.LastFailure(track.MelodyId!)
                    ?? "no plugin could download this track";
                return "failed";
            }

            // Write-through: the file always ends up with full tags,
            // whatever the plugin could or could not read from the source.
            Tagging.TaglibHelper.WriteTags(
                path,
                title: track.Title,
                artist: track.Artist,
                album: track.Album,
                track: (uint?)track.Position);

            // Fast integrity gate: a truncated or corrupt download
            // must not survive as "downloaded": delete it and fail the
            // track so the next run retries it. DurationMs is what the
            // source platform advertised for the track.
            var integrity = Audio.FileIntegrity.Check(
                path, track.DurationMs is > 0 ? TimeSpan.FromMilliseconds(track.DurationMs.Value) : null);
            if (!integrity.Ok)
            {
                try { System.IO.File.Delete(path); } catch { /* best effort */ }
                track.DownloadStatus = "failed";
                track.DownloadError = $"corrupt download: {integrity.Reason}";
                return "failed";
            }

            track.DownloadStatus = "downloaded";
            track.CurrentPath = path;
            track.LastSeenAt = DateTime.UtcNow;
            track.Bitrate = Audio.BitrateProbe.MeasureKbps(path);
            AudioProbe.Fill(track, path);
            track.Warning = BuildWarning(track, path, spectrumMode);
            return "downloaded";
        }
        catch (Exception ex)
        {
            track.DownloadStatus = "failed";
            track.DownloadError = ex.Message.Truncate(1000);
            return "failed";
        }
        finally
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Atomically claims the next pending track of a playlist (status
    /// pending/failed/null -> in_progress) and returns its row id, or
    /// null when nothing is left. The conditional UPDATE is what makes
    /// concurrent claimers safe: two callers never get the same track.
    /// Tracks in <paramref name="exclude"/> were already attempted by this
    /// caller and are skipped, so failed tracks are not retried in-loop.
    /// </summary>
    private async Task<int?> ClaimNextPendingAsync(string playlistId, HashSet<int> exclude, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var trackId = await db.Tracks
            .Where(t => t.PlaylistEntityId == playlistId
                && !exclude.Contains(t.Id)
                && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
            .OrderBy(t => t.Position)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (trackId == 0) return null;

        var claimed = await db.Tracks
            .Where(t => t.Id == trackId
                && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct);
        if (claimed != 1) return null; // lost the race: another caller won it

        exclude.Add(trackId);
        return trackId;
    }

    /// <summary>
    /// Builds the warning shown next to a downloaded track: low search
    /// confidence and spectral doubts from the post-download verification.
    /// </summary>
    private static string? BuildWarning(TrackEntity track, string path, SpectrumMode spectrumMode)
    {
        var warnings = new List<string>();

        if (SpectrumAnalyzer.Verify(path, spectrumMode) is { } spectrum)
        {
            if (spectrum.LooksInflated)
                warnings.Add($"bitrate looks inflated: {spectrum.Note}");
            else if (spectrum.EffectiveKbpsClass is > 0 and < 320)
                warnings.Add($"quality hint: {spectrum.Note}");
        }

        return warnings.Count == 0 ? null : string.Join("; ", warnings);
    }

    /// <summary>
    /// Maps a playlist's PreferredFormat to the waterfall quality.
    /// "preset:saver" | "preset:high" | "preset:lossless" | "auto" name
    /// the shared presets; a raw container ("mp3", "flac", "opus", "aac")
    /// optionally suffixed with a bitrate ("mp3:192" cap, "mp3:192-320"
    /// band, open bands "-320" / "192-") is the advanced path.
    /// </summary>
    internal static DownloadQuality ParseQuality(string? preferredFormat)
        => ParseQualityDetailed(preferredFormat).primary;

    /// <summary>Same mapping, but keeps the preset's fallback rule (Lossless retries lossy).</summary>
    internal static (DownloadQuality primary, DownloadQuality? fallback) ParseQualityDetailed(string? preferredFormat)
    {
        if (QualityPresets.TryParse(preferredFormat, out var preset))
            return QualityPresets.ToQuality(preset);

        var quality = ParseRawQuality(preferredFormat);
        return (quality, null);
    }

    private static DownloadQuality ParseRawQuality(string? preferredFormat)
    {
        preferredFormat ??= "auto";
        var parts = preferredFormat.Split(':', 2);
        var format = parts[0].ToLowerInvariant() switch
        {
            "mp3" => AudioFormat.Mp3,
            "flac" => AudioFormat.Flac,
            "opus" => AudioFormat.Opus,
            "aac" => AudioFormat.Aac,
            _ => AudioFormat.Auto,
        };

        if (parts.Length < 2)
            return new DownloadQuality(format);

        // "192" is a cap; "min-max", "-max" and "min-" are bands.
        var range = parts[1].Split('-', 2);
        var min = range.Length == 2 && int.TryParse(range[0], out var lo) && lo > 0 ? lo : (int?)null;
        var max = int.TryParse(range.Length == 2 ? range[1] : range[0], out var hi) && hi > 0 ? hi : (int?)null;
        return new DownloadQuality(format, min, max);
    }

    /// <summary>All persisted playlists (without tracks), newest sync first.</summary>
    public async Task<List<PlaylistEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists
            .AsNoTracking()
            .OrderByDescending(p => p.LastSyncAt ?? DateTime.MinValue)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Download counters and the bytes the finished files take on disk,
    /// per playlist id, for the playlist cards.
    /// </summary>
    public async Task<Dictionary<string, (int downloaded, int failed, int total, long sizeBytes)>> GetDownloadStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Tracks.AsNoTracking()
            .GroupBy(t => t.PlaylistEntityId)
            .Select(g => new
            {
                Id = g.Key,
                Downloaded = g.Count(t => t.DownloadStatus == "downloaded"),
                Failed = g.Count(t => t.DownloadStatus == "failed"),
                Total = g.Count(),
                SizeBytes = g.Where(t => t.DownloadStatus == "downloaded")
                    .Sum(t => t.FileSizeBytes ?? 0),
            })
            .ToListAsync(ct);

        return rows
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, r => (r.Downloaded, r.Failed, r.Total, r.SizeBytes));
    }

    /// <summary>
    /// The ids of the playlists already saved locally, for marking
    /// imports as duplicates. Matched on source platform + external id,
    /// the same pair a re-import updates instead of duplicating.
    /// </summary>
    public async Task<HashSet<string>> GetSavedExternalIdsAsync(
        Platform platform, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Playlists.AsNoTracking()
            .Where(p => p.SourcePlatform == platform && p.ExternalId != null)
            .Select(p => p.ExternalId!)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>Removes one track from a playlist snapshot and deletes its file.</summary>
    public async Task<bool> RemoveTrackAsync(string playlistId, int trackId, bool deleteFile, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks
            .FirstOrDefaultAsync(t => t.Id == trackId && t.PlaylistEntityId == playlistId, ct);
        if (track is null) return false;

        if (deleteFile && track.CurrentPath is not null)
        {
            try { System.IO.File.Delete(track.CurrentPath); } catch { /* best effort */ }
        }
        db.Tracks.Remove(track);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>One persisted playlist including its current track snapshot.</summary>
    public async Task<PlaylistEntity?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists
            .Include(p => p.Tracks.OrderBy(t => t.Position))
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>
    /// Fetches a playlist: the account path first (private playlists, fresh
    /// metadata), the public path as the independent fallback. The public
    /// fetcher needs no account and keeps working when the auth flow or the
    /// account breaks.
    /// </summary>
    private async Task<Playlist> FetchWithAccountFallbackAsync(
        ISourceProvider provider, string sourceIdentifier, CancellationToken ct)
    {
        var account = _accountProviders.FirstOrDefault(
            a => a.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase));

        if (account is not null && await account.IsConnectedAsync(ct))
        {
            try
            {
                var viaAccount = provider.Platform switch
                {
                    Platform.Spotify when account is SpotifyAccountProvider spotify =>
                        await spotify.TryGetPlaylistViaAccountAsync(
                            SpotifySourceProvider.ExtractPlaylistId(sourceIdentifier), ct),
                    Platform.YouTubeMusic when account is YouTubeAccountProvider youtube =>
                        await youtube.TryGetPlaylistViaAccountAsync(
                            YouTubeSourceProvider.GetPlaylistId(sourceIdentifier), ct),
                    _ => null,
                };
                if (viaAccount is not null) return viaAccount;
                _logger.LogInformation(
                    "Account fetch for '{Source}' returned nothing; falling back to the public path",
                    sourceIdentifier);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "Account fetch for '{Source}' failed ({Message}); falling back to the public path",
                    sourceIdentifier, ex.Message);
            }
        }

        return await provider.GetPlaylistAsync(sourceIdentifier);
    }

    /// <summary>
    /// Fetch a playlist live from its source platform and persist it.
    /// Reuses the existing row when the playlist was saved before (match on
    /// platform + external ID), so re-syncing updates instead of duplicating.
    /// </summary>
    public async Task<PlaylistEntity> AddOrRefreshAsync(string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
        => (await AddOrRefreshDetailedAsync(sourceUrl, targetDirectory, ct)).Playlist;

    /// <summary>
    /// Same fetch, plus whether the playlist was newly saved or updated an
    /// existing row. The add form says "updated" instead of "saved" with it.
    /// </summary>
    public async Task<(PlaylistEntity Playlist, bool NewlySaved)> AddOrRefreshDetailedAsync(
        string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
    {
        var provider = ResolveProvider(sourceUrl)
            ?? throw new InvalidOperationException(
                $"No source provider can handle '{sourceUrl}'. Supported: {string.Join(", ", _providers.Select(p => p.Name))}");

        var playlist = await FetchWithAccountFallbackAsync(provider, sourceUrl, ct);
        return await SaveSnapshotAsync(
            provider.Platform, playlist, sourceUrl, targetDirectory, ct);
    }

    /// <summary>
    /// The logged-in user's liked songs, imported through their account.
    /// </summary>
    public async Task<PlaylistEntity> AddOrRefreshLikedAsync(
        string providerName, string? targetDirectory = null, CancellationToken ct = default)
    {
        var account = _accountProviders.FirstOrDefault(
            a => a.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No account provider '{providerName}'.");

        var playlist = await account.GetLikedPlaylistAsync(ct);
        return (await SaveSnapshotAsync(
            playlist: playlist,
            providerPlatform: account.Name switch
            {
                SpotifyAccountProvider.ProviderName => Platform.Spotify,
                YouTubeAccountProvider.ProviderName => Platform.YouTubeMusic,
                _ => throw new InvalidOperationException($"Unknown account '{providerName}'."),
            },
            sourceUrl: playlist.SourceUrl,
            targetDirectory: targetDirectory,
            ct: ct)).Entity;
    }

    /// <summary>
    /// Imports playlists parsed from a user-provided file (Exportify CSV
    /// or a Spotify privacy export). Each one goes through the normal
    /// snapshot pipeline, so re-imports update instead of duplicating.
    /// Returns (importedPlaylists, totalTracks).
    /// </summary>
    public async Task<(int playlists, int tracks)> ImportFileAsync(
        ImportedFile file, string? targetDirectory = null, CancellationToken ct = default)
    {
        var imported = 0;
        var totalTracks = 0;

        foreach (var incoming in file.Playlists)
        {
            if (incoming.Tracks.Count == 0) continue;

            // Stable identity per imported playlist: re-uploading the
            // same file refreshes (the YourLibrary liked-songs import
            // always lands on spotify:import:liked, kept apart from the
            // OAuth spotify:liked). Manual imports have no live source
            // to pull from, so they count as Unknown platform: the cards
            // say Imported, and refresh/auto-sync leave them alone.
            var slug = Slugify(incoming.Name);
            await SaveSnapshotAsync(
                providerPlatform: Platform.Unknown,
                playlist: new Playlist
                {
                    Id = slug,
                    Name = incoming.Name,
                    Owner = incoming.Owner,
                    Tracks = incoming.Tracks.ToList(),
                },
                sourceUrl: $"spotify:import:{slug}",
                targetDirectory: targetDirectory,
                ct: ct);
            imported++;
            totalTracks += incoming.Tracks.Count;
        }

        _logger.LogInformation("File import ({Kind}): {Playlists} playlists, {Tracks} tracks",
            file.Kind, imported, totalTracks);
        return (imported, totalTracks);
    }

    /// <summary>Filename-stable slug: lowercase letters/digits, rest to '-'.</summary>
    private static string Slugify(string name)
    {
        var slug = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        return slug.ToString().Trim('-') is { Length: > 0 } s ? s : "import";
    }

    private async Task<(PlaylistEntity Entity, bool NewlySaved)> SaveSnapshotAsync(
        Platform providerPlatform, Playlist playlist, string? sourceUrl,
        string? targetDirectory = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.SourcePlatform == providerPlatform &&
                                     p.ExternalId == playlist.Id, ct);

        var isNew = entity is null;
        entity ??= new PlaylistEntity
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = sourceUrl ?? playlist.Id ?? "unknown-source",

            SourcePlatform = providerPlatform,
        };

        entity.Name = playlist.Name ?? entity.Name;
        entity.Description = playlist.Description;
        entity.ExternalId = playlist.Id;
        entity.CoverImageUrl = playlist.CoverImageUrl;
        entity.Owner = playlist.Owner;
        entity.TrackCount = playlist.Tracks?.Count ?? 0;
        entity.LastSyncAt = DateTime.UtcNow;
        entity.LastSyncStatus = SyncStatus.Completed;
        if (targetDirectory is not null)
            entity.TargetDirectory = targetDirectory;
        else if (isNew)
            // Explicit default beats an implicit throw on the first
            // download: a new playlist knows its folder from day one.
            entity.TargetDirectory = await _settings.GetAsync("music_path", "/music", ct);

        // A playlist folder the Library can see: every NEW folder becomes a
        // scan location so freshly downloaded files show up in the library.
        if (isNew && !string.IsNullOrWhiteSpace(entity.TargetDirectory))
            await EnsureScanLocationAsync(db, entity.TargetDirectory, ct);

        // Reconcile the snapshot according to the playlist's SyncMode:
        //  Additive: removed-from-source tracks are kept but flagged.
        //  Mirror   : removed-from-source tracks are deleted entirely.
        var mode = ParseSyncMode(entity.SyncMode);
        var incoming = playlist.Tracks ?? [];
        var incomingIds = incoming
            .Select(t => t.PlatformSongID?.ID ?? t.SongID?.ID)
            .Where(id => id is not null)
            .ToHashSet();

        // Carry over stable identity and download state for tracks that
        // persist across syncs: ExternalId is the join key.
        var existingByExternalId = entity.Tracks
            .Where(t => t.ExternalId is not null)
            .ToDictionary(t => t.ExternalId!, t => t);

        var removed = entity.Tracks
            .Where(t => t.ExternalId is not null && !incomingIds.Contains(t.ExternalId))
            .ToList();

        if (mode == PlaylistSyncMode.Mirror)
        {
            db.Tracks.RemoveRange(removed);
        }
        else
        {
            // Additive: keep history rows, but detach them from the playlist
            // relationship so re-assigning Tracks below does not delete them.
            foreach (var track in removed)
            {
                track.DownloadStatus = "removed-from-source";
                track.PlaylistSnapshotId = track.Id; // remember origin; relationship dropped below
                entity.Tracks.Remove(track);
            }
        }

        // Replace the remaining snapshot with the fresh source state,
        // preserving MelodyId + download state for surviving tracks.
        db.Tracks.RemoveRange(entity.Tracks);
        entity.Tracks = incoming
            .Select((t, i) =>
            {
                var externalId = t.PlatformSongID?.ID ?? t.SongID?.ID;
                var prior = externalId is not null ? existingByExternalId.GetValueOrDefault(externalId) : null;
                var mapped = MapTrack(t, providerPlatform, entity.Id, i);
                mapped.IsLiked = t.IsLiked;
                if (prior is not null)
                {
                    mapped.MelodyId = prior.MelodyId;
                    mapped.CurrentPath = prior.CurrentPath;
                    mapped.DownloadStatus = prior.DownloadStatus;
                    mapped.DownloadError = prior.DownloadError;
                }
                return mapped;
            })
            .ToList();

        if (isNew) db.Playlists.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("{Action} playlist '{Name}' ({Platform}) with {Count} tracks",
            isNew ? "Added" : "Refreshed", entity.Name, providerPlatform, entity.Tracks.Count);

        return (entity, isNew);
    }

    /// <summary>Re-fetch an already persisted playlist from its source.</summary>
    public Task<PlaylistEntity> RefreshAsync(string playlistId, CancellationToken ct = default)
    {
        return RefreshInternal(playlistId, null, ct);
    }

    public Task<PlaylistEntity> RefreshAsync(string playlistId, string targetDirectory, CancellationToken ct = default)
    {
        return RefreshInternal(playlistId, targetDirectory, ct);
    }

    private async Task<PlaylistEntity> RefreshInternal(string playlistId, string? targetDirectory, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

        if (existing.IsManualImport)
            throw new InvalidOperationException(
                $"'{existing.Name}' was imported from a file. Re-import the file to update it.");

        try
        {
            var refreshed = await AddOrRefreshAsync(existing.SourceUrl, targetDirectory, ct);
            return refreshed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refreshing playlist {Id} failed", playlistId);
            await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
            var mark = await db2.Playlists.FindAsync(new object[] { playlistId }, ct);
            if (mark is not null)
            {
                mark.LastSyncStatus = SyncStatus.Failed;
                mark.LastSyncAt = DateTime.UtcNow;
                await db2.SaveChangesAsync(ct);
            }
            throw;
        }
    }

    /// <summary>
    /// Removes a playlist and its snapshot. When deleteFiles is set, the
    /// music files on disk go too; otherwise only the database rows change.
    /// Liked-songs playlists share files with nothing, but a track row can
    /// exist without a file, so deletion is best-effort per path.
    /// </summary>
    public async Task<bool> DeleteAsync(string playlistId, bool deleteFiles = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (entity is null) return false;

        if (deleteFiles)
        {
            foreach (var path in entity.Tracks
                .Where(t => t.CurrentPath is { Length: > 0 })
                .Select(t => t.CurrentPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { System.IO.File.Delete(path); } catch { /* best effort */ }
            }
        }

        db.Tracks.RemoveRange(entity.Tracks);
        db.Playlists.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateSettingsAsync(string playlistId, string? name, bool autoSync, int? intervalMinutes, string? targetDirectory, CancellationToken ct = default)
        => await UpdateSettingsAsync(playlistId, name, autoSync, intervalMinutes, targetDirectory, null, null, ct);

    public async Task UpdateSettingsAsync(string playlistId, string? name, bool autoSync, int? intervalMinutes, string? targetDirectory, PlaylistSyncMode? syncMode, string? preferredFormat = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists.FindAsync(new object[] { playlistId }, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");
        if (name is { Length: > 0 } n) entity.Name = n;
        entity.AutoSyncEnabled = autoSync;
        entity.AutoSyncIntervalMinutes = autoSync ? intervalMinutes : null;
        if (targetDirectory is { Length: > 0 } dir) entity.TargetDirectory = dir;
        if (syncMode is not null) entity.SyncMode = syncMode.Value.ToString();
        if (preferredFormat is { Length: > 0 } fmt)
            entity.PreferredFormat = PlaylistStore.IsValidFormat(fmt) ? fmt : "auto";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Schedule-based variant: the schedule string (empty or a cron
    /// expression, as ScanSchedule stores it) is the single source of
    /// truth; the legacy boolean and minutes columns are kept in sync so
    /// nothing reading them sees a lie.
    /// </summary>
    public async Task UpdateScheduleAsync(
        string playlistId, string? name, ScanSchedule schedule,
        string? targetDirectory = null, PlaylistSyncMode? syncMode = null,
        string? preferredFormat = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists.FindAsync(new object[] { playlistId }, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");
        if (name is { Length: > 0 } n) entity.Name = n;
        entity.ScheduleCron = schedule.ToString();
        entity.AutoSyncEnabled = schedule.Mode != ScanScheduleMode.Manual;
        entity.AutoSyncIntervalMinutes = schedule.Mode == ScanScheduleMode.Interval
            ? schedule.IntervalMinutes
            : null;
        if (targetDirectory is { Length: > 0 } dir) entity.TargetDirectory = dir;
        if (syncMode is not null) entity.SyncMode = syncMode.Value.ToString();
        if (preferredFormat is { Length: > 0 } fmt)
            entity.PreferredFormat = PlaylistStore.IsValidFormat(fmt) ? fmt : "auto";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Base format values shown in the UI dropdown.</summary>
    public static readonly string[] FormatOptions = { "auto", "mp3", "flac", "opus", "aac" };

    /// <summary>Validates a PreferredFormat string ("auto", "preset:saver", "mp3:192", legacy "mp3:192-320").</summary>
    public static bool IsValidFormat(string value)
    {
        if (QualityPresets.IsPreset(value)) return true;
        var parts = value.Split(':', 2);
        if (!FormatOptions.Contains(parts[0].ToLowerInvariant())) return false;
        if (parts.Length == 1) return true;

        var range = parts[1].Split('-', 2);
        foreach (var side in range)
            if (side.Length > 0 && (!int.TryParse(side, out var n) || n <= 0))
                return false;
        return true;
    }

    /// <summary>
    /// All playlists whose auto-sync schedule is due. The schedule is the
    /// shared ScanSchedule string; the legacy boolean is still honoured for
    /// rows that predate it (converted by the schema patcher, but a row
    /// saved by an older build in between gets one more interval run).
    /// Used by the background scheduler.
    /// </summary>
    public async Task<List<PlaylistEntity>> GetDueForAutoSyncAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.Playlists
            .AsNoTracking()
            .Where(p => (p.ScheduleCron != null && p.ScheduleCron != ""
                        || p.AutoSyncEnabled)
                        // File imports have no live source to pull: never due.
                        && !p.SourceUrl.StartsWith("spotify:import:"))
            .ToListAsync(ct);

        return candidates
            .Where(p =>
            {
                var schedule = p.ScheduleCron is { Length: > 0 }
                    ? ScanSchedule.Parse(p.ScheduleCron)
                    : ScanSchedule.FromInterval(p.AutoSyncIntervalMinutes ?? 60);
                var last = p.LastSyncAt is { } at ? new DateTimeOffset(at, TimeSpan.Zero)
                    // Never synced: due immediately so the first run happens.
                    : DateTimeOffset.MinValue;
                return schedule.IsDue(last, now);
            })
            .ToList();
    }

    /// <summary>
    /// Inserts a ScanLocation for <paramref name="path"/> when none exists
    /// (case-insensitive, no duplicates). Manual defaults: live monitoring
    /// off (it spams rescans on big folders), no interval, no cron.
    /// </summary>
    private static async Task EnsureScanLocationAsync(MelodyBridgeDbContext db, string path, CancellationToken ct)
    {
        var exists = await db.ScanLocations
            .AnyAsync(l => l.Path.ToLower() == path.ToLower(), ct);
        if (!exists)
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = path,
                LiveMonitoring = false,
                ScheduleCron = null,
                ScanIntervalHours = null,
            });
    }

    /// <summary>
    /// Backfills audio facts (bitrate, sample rate, file size) on downloaded
    /// tracks from before those columns existed, and flags downloaded tracks
    /// whose file vanished as pending so the next download run picks them up.
    /// Returns (recomputed, missing).
    /// </summary>
    public async Task<(int recomputed, int missing)> RecomputeMissingFactsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stale = await db.Tracks
            .Where(t => t.DownloadStatus == "downloaded"
                && (t.Bitrate == null || t.SampleRateHz == null || t.FileSizeBytes == null)
                && t.CurrentPath != null)
            .ToListAsync(ct);

        var recomputed = 0;
        var missing = 0;
        foreach (var track in stale)
        {
            if (!File.Exists(track.CurrentPath))
            {
                // Same wording LibraryReconciler uses: the UI explains it once.
                track.DownloadStatus = "pending";
                track.Warning = "file missing on disk, will re-download";
                missing++;
                continue;
            }

            track.Bitrate = Audio.BitrateProbe.MeasureKbps(track.CurrentPath!);
            AudioProbe.Fill(track, track.CurrentPath!);
            recomputed++;
        }

        if (recomputed > 0 || missing > 0)
            await db.SaveChangesAsync(ct);
        return (recomputed, missing);
    }

    private static PlaylistSyncMode ParseSyncMode(string? raw)
        => Enum.TryParse<PlaylistSyncMode>(raw, true, out var mode) ? mode : PlaylistSyncMode.Additive;

    /// <summary>Export persisted playlists (with tracks) as JSON for backup.</summary>
    public async Task<string> ExportAsync(IEnumerable<string>? playlistIds = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Playlists.Include(p => p.Tracks).AsNoTracking().AsQueryable();
        if (playlistIds?.Any() == true)
            query = query.Where(p => playlistIds.Contains(p.Id));

        var playlists = await query.ToListAsync(ct);
        return JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>one playlist's track snapshot as utf-8 csv bytes, bom prepended so excel behaves.</summary>
    public async Task<byte[]> ExportCsvAsync(string playlistId, CancellationToken ct = default)
    {
        var entity = await GetByIdAsync(playlistId, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

        var sb = new StringBuilder();
        sb.AppendLine("Position,Title,Artist,Album,DurationMs,Status,BitrateKbps,SampleRateHz,MediaType,FileSizeBytes,Filename");
        foreach (var t in entity.Tracks)
        {
            sb.Append(t.Position + 1).Append(',')
                .Append(Csv.Escape(t.Title)).Append(',')
                .Append(Csv.Escape(t.Artist)).Append(',')
                .Append(Csv.Escape(t.Album)).Append(',')
                .Append(t.DurationMs).Append(',')
                .Append(Csv.Escape(t.DownloadStatus)).Append(',')
                .Append(t.Bitrate).Append(',')
                .Append(t.SampleRateHz).Append(',')
                .Append(Csv.Escape(t.MediaType)).Append(',')
                .Append(t.FileSizeBytes).Append(',')
                .AppendLine(Csv.Escape(t.CurrentPath is null ? string.Empty : Path.GetFileName(t.CurrentPath)));
        }

        // bom first: without it excel guesses a legacy codepage and mangles titles
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>Import playlists from a previous export; existing ones are refreshed instead.</summary>
    public async Task<int> ImportAsync(string json, CancellationToken ct = default)
    {
        var incoming = JsonSerializer.Deserialize<List<PlaylistEntity>>(json);
        if (incoming is null || incoming.Count == 0) return 0;

        var imported = 0;
        foreach (var playlist in incoming.Where(p => !string.IsNullOrWhiteSpace(p.SourceUrl)))
        {
            await AddOrRefreshAsync(playlist.SourceUrl, playlist.TargetDirectory, ct);
            imported++;
        }
        return imported;
    }

    private ISourceProvider? ResolveProvider(string sourceUrl)
    {
        // Prefer an explicit URL match; fall back to the only provider that
        // can parse the identifier.
        return _providers.FirstOrDefault(p => p.CanHandle(sourceUrl));
    }

    private static TrackEntity MapTrack(Track track, Platform platform, string playlistId, int position)
    {
        var externalId = track.PlatformSongID?.ID ?? track.SongID?.ID;
        return new TrackEntity
        {
            MelodyId = $"mb-{Guid.NewGuid():N}",
            ExternalId = externalId,
            ExternalPlatform = platform.ToString(),
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            DurationMs = (long?)track.Duration?.TotalMilliseconds,
            MediaType = track.MediaType.ToString(),
            SourceUrl = track.CurrentTrackLocation?.Path,
            Platform = platform.ToString(),
            Position = position,
            DownloadStatus = "pending",
            PlaylistSnapshotId = null,
            CurrentPath = null,
        };
    }
}

// csv export helpers: cell quoting and download filename sanitizing
public static class Csv
{
    /// <summary>wraps a cell in quotes when it holds a comma, quote or newline; doubles embedded quotes.</summary>
    public static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>playlist name with characters illegal in filenames replaced by '_'.</summary>
    public static string SafeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

internal static class StringTruncateExtensions
{
    public static string? Truncate(this string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? s : (s!.Length <= max ? s : s[..max] + "…");
}
