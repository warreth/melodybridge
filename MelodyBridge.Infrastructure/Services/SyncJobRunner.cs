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
        var skippedTracks = 0;
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var searchPaths = job.SearchLocationPaths.Select(p => new ScanLocation(p));

            // SourceId refers to a PlaylistEntity (the wizard binds playlist
            // IDs). The whole playlist counts as the total: only tracks with
            // a local file are usable in an M3U / media-server playlist, so
            // the run says 20/50 when 30 tracks are not downloaded yet.
            // A null SourceId with search locations means "local folder":
            // every track whose CurrentPath lives under one of them.
            List<TrackEntity> allTracks;
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
                allTracks = sourcePlaylist.Tracks.OrderBy(t => t.Position).ToList();
            }
            else if (job.SearchLocationPaths.Count > 0)
            {
                allTracks = (await db.Tracks
                        .Where(t => t.CurrentPath != null)
                        .ToListAsync(ct))
                    .Where(t => job.SearchLocationPaths.Any(p =>
                        t.CurrentPath!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(t => t.Id)
                    .ToList();
            }
            else
            {
                allTracks = await db.Tracks
                    .Where(t => t.PlaylistSnapshotId == null)
                    .OrderBy(t => t.Position).ThenBy(t => t.Id)
                    .ToListAsync(ct);
            }

            totalTracks = allTracks.Count;
            var matchedTracks = new List<TrackEntity>();
            foreach (var t in allTracks)
            {
                if (t.DownloadStatus == "downloaded" && !string.IsNullOrEmpty(t.CurrentPath))
                {
                    matchedTracks.Add(t);
                }
                else
                {
                    warnings.Add($"{t.Title ?? t.CurrentPath ?? "track"} — no local file");
                }
            }
            skippedTracks = totalTracks - matchedTracks.Count;

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
                case OutputTargetType.PlexApi:
                case OutputTargetType.NavidromeApi:
                    var server = _mediaServers.FirstOrDefault(s =>
                        s.Name.Equals(job.OutputTarget switch
                        {
                            OutputTargetType.JellyfinApi => "Jellyfin",
                            OutputTargetType.PlexApi => "Plex",
                            OutputTargetType.NavidromeApi => "Navidrome",
                            _ => (string?)null
                        }, StringComparison.OrdinalIgnoreCase));
                    if (server == null)
                    {
                        errors.Add($"No plugin registered for '{job.OutputTarget}'");
                        break;
                    }

                    // Per-job connection wins when the wizard stored one;
                    // otherwise the global settings apply (Settings page).
                    MediaServerConnection? connection =
                        !string.IsNullOrWhiteSpace(job.JellyfinServerUrl)
                            ? new MediaServerConnection(
                                job.JellyfinServerUrl!,
                                job.JellyfinApiKey ?? "",
                                job.JellyfinUserId)
                            : null;
                    var serverOptions = new PlaylistOutputOptions("/playlists/" + job.Name + ".m3u",
                        false, remap.Count > 0 ? remap : null, connection);
                    await server.SyncPlaylistAsync(playlist, serverOptions, ct);
                    resolvedTracks = playlist.Tracks?.Count ?? 0;

                    if (server.LastReport?.UnresolvedPaths is { Length: > 0 } unresolved)
                    {
                        warnings.AddRange(unresolved
                            .Select(p => $"{p} — not found on server"));
                    }
                    break;

                default:
                    errors.Add($"Output target '{job.OutputTarget}' not implemented");
                    break;
            }

            // Record the run
            await using (var db2 = await _dbFactory.CreateDbContextAsync(ct))
            {
                var summary = skippedTracks > 0
                    ? $"Synced {resolvedTracks}/{totalTracks} tracks, {skippedTracks} without a local file"
                    : $"Synced {resolvedTracks}/{totalTracks} tracks";

                var runEntity = new SyncJobRunEntity
                {
                    SyncJobId = job.Id,
                    Timestamp = DateTime.UtcNow,
                    Status = errors.Count == 0 ? "Completed" : "Failed",
                    Message = errors.Count > 0 ? string.Join("; ", errors) : summary,
                    ResolvedTracks = resolvedTracks,
                    TotalTracks = totalTracks,
                    WarningDetails = warnings.Count > 0
                        ? JsonSerializer.Serialize(warnings) : null,
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
                List<string>? runWarnings = null;
                try
                {
                    runWarnings = runEntity.WarningDetails is null
                        ? null
                        : JsonSerializer.Deserialize<List<string>>(runEntity.WarningDetails);
                }
                catch { /* tolerate unreadable warnings JSON */ }
                await db2.SaveChangesAsync(ct);

                return new SyncJobRunLog(DateTime.UtcNow,
                    errors.Count == 0 ? SyncStatus.Completed : SyncStatus.Failed,
                    runMessage, resolvedTracks, totalTracks, runWarnings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync job {id} failed", job.Id);
            return new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Failed, ex.Message, 0, totalTracks);
        }
    }
}
