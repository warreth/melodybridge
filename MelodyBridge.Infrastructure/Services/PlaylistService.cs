using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

public interface IPlaylistService
{
    Task<List<PlaylistEntity>> GetAllPlaylistsAsync(CancellationToken ct = default);
    Task<PlaylistEntity?> GetPlaylistByIdAsync(string id, CancellationToken ct = default);
    Task<PlaylistEntity> CreatePlaylistAsync(PlaylistEntity playlist, CancellationToken ct = default);
    Task<PlaylistEntity> UpdatePlaylistAsync(PlaylistEntity playlist, CancellationToken ct = default);
    Task DeletePlaylistAsync(string id, CancellationToken ct = default);
    Task SyncPlaylistAsync(string id, ISourceProvider sourceProvider, CancellationToken ct = default);
}

public class PlaylistService : IPlaylistService
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<PlaylistService> _logger;

    public PlaylistService(IDbContextFactory<MelodyBridgeDbContext> dbFactory, ILogger<PlaylistService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<PlaylistEntity>> GetAllPlaylistsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists.AsNoTracking().ToListAsync(ct);
    }

    public async Task<PlaylistEntity?> GetPlaylistByIdAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<PlaylistEntity> CreatePlaylistAsync(PlaylistEntity playlist, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Created playlist: {PlaylistName}", playlist.Name);
        return playlist;
    }

    public async Task<PlaylistEntity> UpdatePlaylistAsync(PlaylistEntity playlist, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Playlists.Update(playlist);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Updated playlist: {PlaylistName}", playlist.Name);
        return playlist;
    }

    public async Task DeletePlaylistAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist != null)
        {
            db.Playlists.Remove(playlist);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Deleted playlist: {PlaylistName}", playlist.Name);
        }
    }

    public async Task SyncPlaylistAsync(string id, ISourceProvider sourceProvider, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        
        if (playlist == null)
        {
            _logger.LogWarning("Playlist not found: {PlaylistId}", id);
            return;
        }

        try
        {
            _logger.LogInformation("Syncing playlist: {PlaylistName}", playlist.Name);
            
            var sourcePlaylist = await sourceProvider.GetPlaylistAsync(playlist.SourceUrl);
            if (sourcePlaylist == null)
            {
                playlist.LastSyncStatus = SyncStatus.Failed;
                _logger.LogWarning("Failed to fetch playlist from source: {SourceUrl}", playlist.SourceUrl);
            }
            else
            {
                playlist.Tracks = sourcePlaylist.Tracks?.Select(t => new TrackEntity
                {
                    Title = t.Title,
                    Artist = t.Artist,
                    SourceUrl = null,
                    Platform = sourceProvider.Platform.ToString()
                }).ToList() ?? new();
                playlist.TrackCount = playlist.Tracks.Count;
                playlist.LastSyncAt = DateTime.UtcNow;
                playlist.LastSyncStatus = SyncStatus.Completed;
                _logger.LogInformation("Successfully synced playlist: {PlaylistName} with {TrackCount} tracks", 
                    playlist.Name, playlist.Tracks.Count);
            }
        }
        catch (Exception ex)
        {
            playlist.LastSyncStatus = SyncStatus.Failed;
            _logger.LogError(ex, "Error syncing playlist: {PlaylistName}", playlist.Name);
        }

        db.Playlists.Update(playlist);
        await db.SaveChangesAsync(ct);
    }
}
