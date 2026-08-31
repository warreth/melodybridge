using System.Text.Json;
using MelodyBridge.Core;
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
    private readonly IEnumerable<ISourceProvider> _providers;
    private readonly IDownloadManager _downloadManager;
    private readonly ILogger<PlaylistStore> _logger;

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
        ILogger<PlaylistStore> logger)
    {
        _dbFactory = dbFactory;
        _providers = providers;
        _downloadManager = downloadManager;
        _logger = logger;
    }

    /// <summary>
    /// Download every track of a playlist that has no local file yet, into the
    /// playlist's TargetDirectory (or override). Updates DownloadStatus/
    /// CurrentPath per track as it goes. Returns (downloaded, failed).
    /// </summary>
    public async Task<(int downloaded, int failed)> DownloadMissingAsync(
        string playlistId, string? outputDirectoryOverride = null, int limit = int.MaxValue, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

        var dir = outputDirectoryOverride ?? entity.TargetDirectory
            ?? throw new InvalidOperationException(
                "No download folder configured. Set one in the playlist settings.");

        var pending = entity.Tracks
            .Where(t => t.DownloadStatus is null or "pending" or "failed")
            .OrderBy(t => t.Position)
            .Take(limit)
            .ToList();

        var downloaded = 0;
        var failed = 0;
        var spectrumMode = SpectrumVerification();

        foreach (var track in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
            {
                track.DownloadStatus = "failed";
                track.DownloadError = "missing artist/title metadata";
                failed++;
                continue;
            }

            track.DownloadStatus = "in_progress";
            await db.SaveChangesAsync(ct);

            try
            {
                var path = await _downloadManager.DownloadTrackAsync(
                    track.Artist!, track.Title!, dir, track.MelodyId!,
                    ParseQuality(entity.PreferredFormat), ct);

                if (path is null)
                {
                    track.DownloadStatus = "failed";
                    track.DownloadError = "no plugin could download this track";
                    failed++;
                }
                else
                {
                    // Write-through: the file always ends up with full tags,
                    // whatever the plugin could or could not read from the source.
                    Tagging.TaglibHelper.WriteTags(
                        path,
                        title: track.Title,
                        artist: track.Artist,
                        album: track.Album,
                        track: (uint?)track.Position);

                    track.DownloadStatus = "downloaded";
                    track.CurrentPath = path;
                    track.LastSeenAt = DateTime.UtcNow;
                    track.Warning = BuildWarning(track, path, spectrumMode);
                    downloaded++;
                }
            }
            catch (Exception ex)
            {
                track.DownloadStatus = "failed";
                track.DownloadError = ex.Message.Truncate(1000);
                failed++;
            }

            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Download run for '{Playlist}' ({Quality}): {Downloaded} downloaded, {Failed} failed",
            entity.Name, entity.PreferredFormat, downloaded, failed);

        return (downloaded, failed);
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
    /// Format: "auto" | "mp3" | "flac" | "opus" | "aac", optionally
    /// suffixed with a bitrate range: "mp3:192-320", "mp3:-320", "mp3:320-".
    /// </summary>
    internal static DownloadQuality ParseQuality(string? preferredFormat)
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
            return new DownloadQuality(format, format == AudioFormat.Auto ? 0 : 128);

        // "min-max", "-max" (no floor), "min-" (no ceiling).
        var range = parts[1].Split('-', 2);
        var min = int.TryParse(range[0], out var lo) && lo > 0 ? lo : 0;
        var max = range.Length == 2 && int.TryParse(range[1], out var hi) && hi > 0 ? hi : (int?)null;
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
    /// Fetch a playlist live from its source platform and persist it.
    /// Reuses the existing row when the playlist was saved before (match on
    /// platform + external ID), so re-syncing updates instead of duplicating.
    /// </summary>
    public async Task<PlaylistEntity> AddOrRefreshAsync(string sourceUrl, string? targetDirectory = null, CancellationToken ct = default)
    {
        var provider = ResolveProvider(sourceUrl)
            ?? throw new InvalidOperationException(
                $"No source provider can handle '{sourceUrl}'. Supported: {string.Join(", ", _providers.Select(p => p.Name))}");

        var playlist = await provider.GetPlaylistAsync(sourceUrl);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.SourcePlatform == provider.Platform &&
                                     p.ExternalId == playlist.Id, ct);

        var isNew = entity is null;
        entity ??= new PlaylistEntity
        {
            Id = Guid.NewGuid().ToString(),
            SourceUrl = sourceUrl,
            SourcePlatform = provider.Platform,
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

        // Reconcile the snapshot according to the playlist's SyncMode:
        //  Additive — removed-from-source tracks are kept but flagged.
        //  Mirror    — removed-from-source tracks are deleted entirely.
        var mode = ParseSyncMode(entity.SyncMode);
        var incoming = playlist.Tracks ?? [];
        var incomingIds = incoming
            .Select(t => t.PlatformSongID?.ID ?? t.SongID?.ID)
            .Where(id => id is not null)
            .ToHashSet();

        // Carry over stable identity and download state for tracks that
        // persist across syncs — ExternalId is the join key.
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
                var mapped = MapTrack(t, provider.Platform, entity.Id, i);
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
            isNew ? "Added" : "Refreshed", entity.Name, provider.Platform, entity.Tracks.Count);

        return entity;
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

    public async Task<bool> DeleteAsync(string playlistId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (entity is null) return false;

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

    /// <summary>Base format values shown in the UI dropdown.</summary>
    public static readonly string[] FormatOptions = { "auto", "mp3", "flac", "opus", "aac" };

    /// <summary>Validates a PreferredFormat string ("mp3", "mp3:192-320", ...).</summary>
    public static bool IsValidFormat(string value)
    {
        var parts = value.Split(':', 2);
        if (!FormatOptions.Contains(parts[0].ToLowerInvariant())) return false;
        if (parts.Length == 1) return true;

        var range = parts[1].Split('-', 2);
        if (range.Length > 2) return false;
        foreach (var side in range)
            if (side.Length > 0 && (!int.TryParse(side, out var n) || n <= 0))
                return false;
        return true;
    }

    /// <summary>
    /// All playlists whose auto-sync interval has elapsed.
    /// Used by the background scheduler.
    /// </summary>
    public async Task<List<PlaylistEntity>> GetDueForAutoSyncAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        return await db.Playlists
            .AsNoTracking()
            .Where(p => p.AutoSyncEnabled
                && (p.LastSyncAt == null
                    || p.LastSyncAt <= now.AddMinutes(-(p.AutoSyncIntervalMinutes ?? 60))))
            .ToListAsync(ct);
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

internal static class StringTruncateExtensions
{
    public static string? Truncate(this string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? s : (s!.Length <= max ? s : s[..max] + "…");
}
