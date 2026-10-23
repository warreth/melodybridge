using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.MediaServers;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

public class SyncJobRunner : ISyncJobRunner
{
    /// <summary>Human name of a target for messages and the run summary.</summary>
    private static string TargetLabel(SyncTarget target) => target.Kind switch
    {
        OutputTargetType.JellyfinApi => "Jellyfin",
        OutputTargetType.PlexApi => "Plex",
        OutputTargetType.SubsonicApi => "Subsonic",
        _ => "M3U file",
    };

    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly M3uGenerator _m3uGenerator;
    private readonly IEnumerable<IMediaServerSync> _mediaServers;
    private readonly ILogger<SyncJobRunner> _logger;
    private readonly MediaServerProfileStore? _profiles;

    public SyncJobRunner(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        M3uGenerator m3uGenerator,
        IEnumerable<IMediaServerSync> mediaServers,
        ILogger<SyncJobRunner> logger,
        MediaServerProfileStore? profiles = null)
    {
        _dbFactory = dbFactory;
        _m3uGenerator = m3uGenerator;
        _mediaServers = mediaServers;
        _logger = logger;
        _profiles = profiles;
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
                    await RecordFailedRunAsync(job,
                        $"Playlist '{job.SourceId}' not found", 0, 0, ct);
                    return new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Failed,
                        $"Playlist '{job.SourceId}' not found", 0, 0);
                }
                allTracks = sourcePlaylist.Tracks.OrderBy(t => t.Position).ToList();
            }
            else if (job.SearchLocationPaths.Count > 0)
            {
                // The folder filter belongs in SQL when the job names a
                // handful of roots: one parameterized prefix per root,
                // OR-ed, all evaluated inside SQLite. StartsWith (no
                // comparison argument) translates to LIKE; SQLite's LIKE
                // folds ASCII case, the C# path folded every letter, so a
                // root whose casing matters beyond ASCII loses the fold
                // -- rare and spelled out here. A dozen or more roots
                // stay on the in-memory path instead of a huge OR chain.
                var roots = job.SearchLocationPaths;
                if (roots.Count <= 4)
                {
                    var filter = PredicateExpr(roots);
                    allTracks = await db.Tracks
                        .Where(filter)
                        .Where(t => t.CurrentPath != null)
                        .OrderBy(t => t.Id)
                        .ToListAsync(ct);
                }
                else
                {
                    var loaded = await db.Tracks
                        .Where(t => t.CurrentPath != null)
                        .ToListAsync(ct);
                    allTracks = loaded
                        .Where(t => roots.Any(root =>
                            t.CurrentPath!.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(t => t.Id)
                        .ToList();
                }
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
                    warnings.Add($"{t.Title ?? t.CurrentPath ?? "track"}: no local file");
                }
            }
            skippedTracks = totalTracks - matchedTracks.Count;

            // Output path rules: the prefix map for mounts, the extension
            // map for formats. Both travel on the options and are applied
            // by the M3U generator and the media server clients; the job's
            // own dictionaries are never mutated.
            var remap = job.PathRemapRules;
            var extensionRemap = job.ExtensionRemapRules.Count > 0 ? job.ExtensionRemapRules : null;

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

            // One publication per target: a job may write the M3U and push
            // to several media servers in the same run. A job stored before
            // multi-target support has no list and falls back to its one
            // legacy target.
            var targets = job.Targets.Count > 0
                ? job.Targets
                : new List<SyncTarget>
                {
                    new(job.OutputTarget,
                        job.OutputTarget == OutputTargetType.M3uFile ? null : job.MediaServerProfileId,
                        job.OutputTarget == OutputTargetType.M3uFile ? job.M3uOutputPath : null),
                };

            resolvedTracks = playlist.Tracks?.Count ?? 0;
            var targetLabels = new List<string>();

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await PublishTargetAsync(target, job, playlist, searchPaths,
                        remap.Count > 0 ? remap : null, extensionRemap, ct);
                    if (result.Label is not null) targetLabels.Add(result.Label);
                    warnings.AddRange(result.Warnings);
                    if (result.Error is not null) errors.Add(result.Error);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A target that throws (an unwritable M3U path, a server
                    // client bug) must not cost the other targets their
                    // publication. Record it and keep going.
                    _logger.LogWarning(ex, "Sync target {Target} of job {Job} failed",
                        TargetLabel(target), job.Id);
                    errors.Add($"{TargetLabel(target)}: {ex.Message}");
                }
            }

            // Record the run
            await using (var db2 = await _dbFactory.CreateDbContextAsync(ct))
            {
                var targetWord = targets.Count == 1 ? "target" : "targets";
                var targetList = targetLabels.Count > 0 ? string.Join(", ", targetLabels) : "none";
                var summary = skippedTracks > 0
                    ? $"Synced {resolvedTracks}/{totalTracks} tracks to {targets.Count} {targetWord} ({targetList}), {skippedTracks} without a local file"
                    : $"Synced {resolvedTracks}/{totalTracks} tracks to {targets.Count} {targetWord} ({targetList})";

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
                    jobEntity.LastRunResolvedTracks = resolvedTracks;
                    jobEntity.LastRunTotalTracks = totalTracks;
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
            // Record it like every other run: a run row and the job's
            // LastRun stamps are what keep the scheduler from treating an
            // unbuilt run as "never ran". The record is best effort: the
            // database being the failed thing must not mask the original
            // error.
            await RecordFailedRunAsync(job, ex.Message, 0, totalTracks, ct);
            return new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Failed, ex.Message, 0, totalTracks);
        }
    }


    private static Expression<Func<TrackEntity, bool>> PredicateExpr(IReadOnlyList<string> roots)
    {
        if (roots.Count == 1)
        {
            var first = roots[0];
            return t => t.CurrentPath != null
                && t.CurrentPath.StartsWith(first);
        }
        if (roots.Count == 2)
        {
            var first = roots[0];
            var second = roots[1];
            return t => t.CurrentPath != null
                && (t.CurrentPath.StartsWith(first)
                    || t.CurrentPath.StartsWith(second));
        }
        if (roots.Count == 3)
        {
            var first = roots[0];
            var second = roots[1];
            var third = roots[2];
            return t => t.CurrentPath != null
                && (t.CurrentPath.StartsWith(first)
                    || t.CurrentPath.StartsWith(second)
                    || t.CurrentPath.StartsWith(third));
        }
        var a = roots[0];
        var b = roots[1];
        var c = roots[2];
        var d = roots[3];
        return t => t.CurrentPath != null
            && (t.CurrentPath.StartsWith(a)
                || t.CurrentPath.StartsWith(b)
                || t.CurrentPath.StartsWith(c)
                || t.CurrentPath.StartsWith(d));
    }

    /// <summary>
    /// Writes the run row and the job's LastRun stamps for a run that
    /// failed before its normal recording block ran. Best effort: when
    /// this write fails too, the original failure is the one to report.
    /// </summary>
    private async Task RecordFailedRunAsync(
        SyncJob job, string message, int resolvedTracks, int totalTracks, CancellationToken ct)
    {
        void Stamp(SyncJobEntity? jobEntity, SyncJobRunEntity runEntity)
        {
            if (jobEntity is null) return;
            jobEntity.LastRunStatus = "Failed";
            jobEntity.LastRunAt = DateTime.UtcNow;
            jobEntity.LastRunSummary = runEntity.Message;
            jobEntity.LastRunResolvedTracks = resolvedTracks;
            jobEntity.LastRunTotalTracks = totalTracks;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var runEntity = new SyncJobRunEntity
            {
                SyncJobId = job.Id,
                Timestamp = DateTime.UtcNow,
                Status = "Failed",
                Message = message.Truncate(1000),
                ResolvedTracks = resolvedTracks,
                TotalTracks = totalTracks,
            };
            db.SyncJobRuns.Add(runEntity);
            var jobEntity = await db.SyncJobs.FindAsync(new object[] { job.Id }, ct);
            Stamp(jobEntity, runEntity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception recordFailure)
        {
            _logger.LogWarning(recordFailure,
                "Could not record the failed run of sync job {Id}", job.Id);
        }
    }
    /// <summary>
    /// Publishes the playlist to one target. Returns what to record instead
    /// of touching the run's error list, so the caller can survive a target
    /// that throws and still publish the others.
    /// </summary>
    private async Task<TargetRunResult> PublishTargetAsync(
        SyncTarget target, SyncJob job, Playlist playlist,
        IEnumerable<ScanLocation> searchPaths,
        Dictionary<string, string>? remap,
        Dictionary<string, string>? extensionRemap,
        CancellationToken ct)
    {
        if (target.Kind == OutputTargetType.M3uFile)
        {
            if (string.IsNullOrWhiteSpace(target.M3uPath))
                return new TargetRunResult(null, Array.Empty<string>(), "M3U file: no output path configured");
            var options = new PlaylistOutputOptions(target.M3uPath, false,
                remap, ExtensionRemap: extensionRemap);
            await _m3uGenerator.GenerateM3uAsync(playlist, searchPaths, options, ct);
            return new TargetRunResult("M3U file", Array.Empty<string>(), null);
        }

        var serverName = target.Kind switch
        {
            OutputTargetType.JellyfinApi => "Jellyfin",
            OutputTargetType.PlexApi => "Plex",
            OutputTargetType.SubsonicApi => "Subsonic",
            _ => (string?)null,
        };
        var server = _mediaServers.FirstOrDefault(s =>
            s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server == null)
            return new TargetRunResult(null, Array.Empty<string>(),
                $"{TargetLabel(target)}: no {serverName} plugin registered");

        // Connection: a media server profile (Settings) is resolved fresh on
        // every run, so editing the profile applies here; legacy jobs with
        // inline values keep working. Without either, the app-wide settings
        // apply.
        MediaServerConnection? connection = null;
        string? profileName = null;
        if (!string.IsNullOrWhiteSpace(target.ProfileId))
        {
            if (_profiles is null)
                return new TargetRunResult(null, Array.Empty<string>(),
                    $"{TargetLabel(target)}: media server profile '{target.ProfileId}' cannot be resolved: no profile store is available.");
            var profile = await _profiles.FindAsync(target.ProfileId!, ct);
            if (profile is null)
                return new TargetRunResult(null, Array.Empty<string>(),
                    $"{TargetLabel(target)}: profile '{target.ProfileId}' no longer exists. Pick another one in the job's settings.");
            if (!MediaServerKinds.MatchesOutputTarget(profile.Kind, target.Kind))
            {
                // The profile kind can be edited after the job was created;
                // a Plex profile must not be fed to the Jellyfin client.
                return new TargetRunResult(null, Array.Empty<string>(),
                    $"{TargetLabel(target)}: profile '{profile.Name}' is a {profile.Kind} server, but this job targets {target.Kind}. Pick a matching profile in the job's settings.");
            }
            // Subsonic authenticates with the username stored on the
            // profile; the password comes from the same profile, so its
            // username wins over a legacy job's user field. Jellyfin keeps
            // the job's user id: it selects whose favorites get marked.
            var userId = job.JellyfinUserId;
            if (MediaServerKinds.IsSubsonic(profile.Kind)
                && !string.IsNullOrWhiteSpace(profile.Username))
            {
                userId = profile.Username;
            }
            connection = new MediaServerConnection(profile.BaseUrl, profile.ApiKey, userId);
            profileName = profile.Name;
        }
        else if (!string.IsNullOrWhiteSpace(job.JellyfinServerUrl))
        {
            connection = new MediaServerConnection(
                job.JellyfinServerUrl!, job.JellyfinApiKey ?? "", job.JellyfinUserId);
        }

        var label = profileName is { Length: > 0 }
            ? $"{TargetLabel(target)} ({profileName})"
            : TargetLabel(target);
        var serverOptions = new PlaylistOutputOptions("/playlists/" + job.Name + ".m3u",
            false, remap, connection, extensionRemap);
        await server.SyncPlaylistAsync(playlist, serverOptions, ct);

        var warnings = server.LastReport?.UnresolvedPaths is { Length: > 0 } unresolved
            ? unresolved.Select(p => $"{p}: not found on {label}").ToList()
            : new List<string>();
        // A sync that failed on the server (unreachable, rejected create or
        // update) must fail the job, not report success.
        var error = server.LastReport is { Succeeded: false } failedReport
            ? $"{label}: {failedReport.Message}"
            : null;
        return new TargetRunResult(label, warnings, error);
    }

    /// <summary>What one target publication produced: its name for the
    /// summary, warnings for the run log, and the failure when it reported
    /// one.</summary>
    private sealed record TargetRunResult(
        string? Label, IReadOnlyList<string> Warnings, string? Error);

}
