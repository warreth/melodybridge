using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

public class MusicSourceManager : IMusicSourceManager
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly IEnumerable<ISourceProvider> _sourceProviders;
    private readonly IDownloadManager _downloadManager;
    private readonly ILogger<MusicSourceManager> _logger;

    public MusicSourceManager(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IEnumerable<ISourceProvider> sourceProviders,
        IDownloadManager downloadManager,
        ILogger<MusicSourceManager> logger)
    {
        _dbFactory = dbFactory;
        _sourceProviders = sourceProviders;
        _downloadManager = downloadManager;
        _logger = logger;
    }

    public async Task<MusicSource> AddSourceAsync(MusicSource source)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var entity = new SourceEntity
        {
            Id = source.Id,
            Name = source.Name,
            Platform = source.Platform.ToString(),
            SourceUrl = source.SourceUrl,
            TargetDirectory = source.TargetDirectory,
            AutoSyncEnabled = source.AutoSyncEnabled,
            AutoSyncIntervalMinutes = source.AutoSyncIntervalMinutes,
            Status = source.Status.ToString(),
        };

        db.Sources.Add(entity);
        await db.SaveChangesAsync();

        // If auto-sync is enabled, trigger initial download
        if (source.AutoSyncEnabled)
        {
            try
            {
                await SyncSourceAsync(source.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial auto-sync failed for source {id}", source.Id);
            }
        }

        return source;
    }

    public async Task RemoveSourceAsync(string sourceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Sources.FindAsync(sourceId);
        if (entity != null)
        {
            db.Sources.Remove(entity);
            await db.SaveChangesAsync();
        }
    }

    public async Task<IReadOnlyList<MusicSource>> GetAllSourcesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.Sources.ToListAsync();
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<MusicSource?> GetSourceAsync(string sourceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Sources.FindAsync(sourceId);
        return entity != null ? MapToDomain(entity) : null;
    }

    public async Task UpdateSourceAsync(MusicSource source)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Sources.FindAsync(source.Id);
        if (entity != null)
        {
            entity.Name = source.Name;
            entity.Platform = source.Platform.ToString();
            entity.SourceUrl = source.SourceUrl;
            entity.TargetDirectory = source.TargetDirectory;
            entity.AutoSyncEnabled = source.AutoSyncEnabled;
            entity.AutoSyncIntervalMinutes = source.AutoSyncIntervalMinutes;
            entity.Status = source.Status.ToString();
            await db.SaveChangesAsync();
        }
    }

    public async Task AutoSyncAllAsync(CancellationToken ct = default)
    {
        var sources = await GetAllSourcesAsync();
        foreach (var source in sources.Where(s => s.AutoSyncEnabled))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await SyncSourceAsync(source.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-sync failed for source {id}", source.Id);
            }
        }
    }

    private async Task SyncSourceAsync(string sourceId)
    {
        var source = await GetSourceAsync(sourceId);
        if (source == null) return;

        var provider = _sourceProviders.FirstOrDefault(p =>
            p.Platform == source.Platform);

        if (provider == null)
        {
            _logger.LogWarning("No source provider for platform {platform}", source.Platform);
            return;
        }

        var playlist = await provider.GetPlaylistAsync(source.SourceUrl);
        if (playlist.Tracks == null || playlist.Tracks.Count == 0)
        {
            _logger.LogWarning("No tracks found in playlist for source {id}", sourceId);
            return;
        }

        var targetDir = source.TargetDirectory ?? "./downloads";

        var downloaded = 0;
        foreach (var track in playlist.Tracks)
        {
            var url = track.CurrentTrackLocation?.Path;
            if (string.IsNullOrEmpty(url)) continue;

            var melodyId = track.SongID?.ID ?? Guid.NewGuid().ToString();
            var result = await _downloadManager.DownloadAsync(url, targetDir, melodyId);

            if (result != null)
                downloaded++;
        }

        source.Status = SyncStatus.Completed;
        source.LastSyncAt = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Sources.FindAsync(sourceId);
        if (entity != null)
        {
            entity.Status = SyncStatus.Completed.ToString();
            entity.LastSyncAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        _logger.LogInformation("Synced source {id}: downloaded {count}/{total} tracks",
            sourceId, downloaded, playlist.Tracks.Count);
    }

    private static MusicSource MapToDomain(SourceEntity entity)
    {
        return new MusicSource
        {
            Id = entity.Id,
            Name = entity.Name,
            Platform = Enum.TryParse<Platform>(entity.Platform, true, out var p) ? p : Platform.YouTubeMusic,
            SourceUrl = entity.SourceUrl,
            TargetDirectory = entity.TargetDirectory,
            AutoSyncEnabled = entity.AutoSyncEnabled,
            AutoSyncIntervalMinutes = entity.AutoSyncIntervalMinutes,
            LastSyncAt = entity.LastSyncAt,
            Status = Enum.TryParse<SyncStatus>(entity.Status, true, out var s) ? s : SyncStatus.Pending,
        };
    }
}
