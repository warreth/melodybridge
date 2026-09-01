using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

public class SyncJobRunner : ISyncJobRunner
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly M3uGenerator _m3uGenerator;
    private readonly IEnumerable<IMediaServerSync> _mediaServers;
    private readonly ILogger<SyncJobRunner> _logger;

    public SyncJobRunner(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        M3uGenerator m3uGenerator,
        IEnumerable<IMediaServerSync> mediaServers,
        ILogger<SyncJobRunner> logger)
    {
        _dbFactory = dbFactory;
        _m3uGenerator = m3uGenerator;
        _mediaServers = mediaServers;
        _logger = logger;
    }

    public async Task<SyncJobRunLog> RunJobAsync(SyncJob job, CancellationToken ct = default)
    {
        var totalTracks = 0;
        var resolvedTracks = 0;
        var errors = new List<string>();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var searchPaths = job.SearchLocationPaths.Select(p => new ScanLocation(p));

            // SourceId refers to a PlaylistEntity (the wizard binds playlist IDs).
            // Only tracks with a local file are usable in an M3U / media-server playlist.
            List<TrackEntity> matchedTracks;
            if (!string.IsNullOrEmpty(job.SourceId))
            {
                var sourcePlaylist = await db.Playlists
                    .Include(p => p.Tracks)
                    .FirstOrDefaultAsync(p => p.Id == job.SourceId, ct);
                if (sourcePlaylist == null)
                {
                    return new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Failed,
                        $"Playlist '{job.SourceId}' not found", 0, 0);
                }
                matchedTracks = sourcePlaylist.Tracks
                    .Where(t => t.DownloadStatus == "downloaded" && !string.IsNullOrEmpty(t.CurrentPath))
                    .OrderBy(t => t.Position)
                    .ToList();
            }
            else
            {
                matchedTracks = await db.Tracks
                    .Where(t => t.DownloadStatus == "downloaded" && !string.IsNullOrEmpty(t.CurrentPath))
                    .OrderBy(t => t.PlaylistSnapshotId).ThenBy(t => t.Position)
                    .ToListAsync(ct);
            }

            totalTracks = matchedTracks.Count;

            // Apply path remapping
            var remap = job.PathRemapRules;

            // Apply extension remapping
            if (job.ExtensionRemapRules.Count > 0)
            {
                foreach (var kv in job.ExtensionRemapRules)
                {
                    remap[kv.Key] = kv.Value;
                }
            }

            var playlist = new Playlist
            {
                Name = job.Name,
                Tracks = matchedTracks.Select(t => new Track
                {
                    Title = t.Title,
                    Duration = t.DurationMs is > 0
                        ? TimeSpan.FromMilliseconds(t.DurationMs.Value)
                        : null,
                    Artist = t.Artist,
                    SongID = !string.IsNullOrEmpty(t.MelodyId)
                        ? new SongID(Platform.Unknown, t.MelodyId)
                        : null,
                    MediaType = Enum.TryParse<MediaType>(t.MediaType, true, out var mt)
                        ? mt : MediaType.UNKNOWN,
                    CurrentTrackLocation = !string.IsNullOrEmpty(t.CurrentPath)
                        ? new FileLocation(t.CurrentPath)
                        : null,
                    IsLiked = t.IsLiked,
                }).ToList()
            };

            // Output based on target type
            switch (job.OutputTarget)
            {
                case OutputTargetType.M3uFile:
                    if (!string.IsNullOrEmpty(job.M3uOutputPath))
                    {
                        var options = new PlaylistOutputOptions(job.M3uOutputPath, false,
                            remap.Count > 0 ? remap : null);
                        await _m3uGenerator.GenerateM3uAsync(playlist, searchPaths, options, ct);
                        resolvedTracks = playlist.Tracks?.Count ?? 0;
                    }
                    break;

                case OutputTargetType.JellyfinApi:
                    var jellyfin = _mediaServers.FirstOrDefault(s =>
                        s.Name.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase));
                    if (jellyfin != null)
                    {
                        var jfOptions = new PlaylistOutputOptions("/playlists/" + job.Name + ".m3u",
                            false, remap.Count > 0 ? remap : null);
                        await jellyfin.SyncPlaylistAsync(playlist, jfOptions, ct);
                        resolvedTracks = playlist.Tracks?.Count ?? 0;
                    }
                    break;

                default:
                    errors.Add($"Output target '{job.OutputTarget}' not implemented");
                    break;
            }

            // Record the run
            await using (var db2 = await _dbFactory.CreateDbContextAsync(ct))
            {
                var runEntity = new SyncJobRunEntity
                {
                    SyncJobId = job.Id,
                    Timestamp = DateTime.UtcNow,
                    Status = errors.Count == 0 ? "Completed" : "Failed",
                    Message = errors.Count > 0 ? string.Join("; ", errors) : $"Synced {resolvedTracks}/{totalTracks} tracks",
                    ResolvedTracks = resolvedTracks,
                    TotalTracks = totalTracks,
                };
                db2.SyncJobRuns.Add(runEntity);

                // Update job
                var jobEntity = await db2.SyncJobs.FindAsync(job.Id);
                if (jobEntity != null)
                {
                    jobEntity.LastRunStatus = errors.Count == 0 ? "Completed" : "Failed";
                    jobEntity.LastRunAt = DateTime.UtcNow;
                    jobEntity.LastRunSummary = runEntity.Message;
                }

                // Capture message before scope exits
                var runMessage = runEntity.Message ?? "";
                await db2.SaveChangesAsync(ct);

                return new SyncJobRunLog(DateTime.UtcNow,
                    errors.Count == 0 ? SyncStatus.Completed : SyncStatus.Failed,
                    runMessage, resolvedTracks, totalTracks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync job {id} failed", job.Id);
            return new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Failed, ex.Message, 0, totalTracks);
        }
    }
}
