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
            if (job.SourceId is { Length: > 0 } sourceId)
            {
                var exists = await db.Playlists.AsNoTracking()
                    .AnyAsync(p => p.Id == sourceId, ct);
                if (!exists)
                {
                    await RecordFailedRunAsync(job,
                        $"Playlist '{sourceId}' not found", 0, 0, ct);
                    return new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Failed,
                        $"Playlist '{sourceId}' not found", 0, 0);
                }
                // Only the columns the publication reads: the row's lyrics
                // and cover text can be several KB per track, and a sync
                // job never looks at them.
                allTracks = await db.Tracks.AsNoTracking()
                    .Where(t => t.PlaylistEntityId == sourceId)
                    .OrderBy(t => t.Position).ThenBy(t => t.Id)
                    .Select(SyncTrackColumns)
                    .ToListAsync(ct);
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
                    allTracks = await db.Tracks.AsNoTracking()
                        .Where(filter)
                        .Where(t => t.CurrentPath != null)
                        .OrderBy(t => t.Id)
                        .Select(SyncTrackColumns)
                        .ToListAsync(ct);
                }
                else
                {
                    var loaded = await db.Tracks.AsNoTracking()
                        .Where(t => t.CurrentPath != null)
                        .OrderBy(t => t.Id)
                        .Select(SyncTrackColumns)
                        .ToListAsync(ct);
                    allTracks = loaded
                        .Where(t => roots.Any(root => IsUnderRoot(t.CurrentPath!, root)))
                        .OrderBy(t => t.Id)
                        .ToList();
                }
            }
            else
            {
                allTracks = await db.Tracks.AsNoTracking()
                    .Where(t => t.PlaylistSnapshotId == null)
                    .OrderBy(t => t.Position).ThenBy(t => t.Id)
                    .Select(SyncTrackColumns)
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

            // One publication per target: a job may write the M3U and push
            // to several media servers in the same run. A job stored before
            // multi-target support has no list and falls back to its one
            // legacy target.
            var targets = job.Targets.Count > 0
                ? job.Targets
                : job.HasExplicitTargets
                    ? new List<SyncTarget>()
                    : new List<SyncTarget>
                    {
                        new(job.OutputTarget,
                            job.OutputTarget == OutputTargetType.M3uFile ? null : job.MediaServerProfileId,
                            job.OutputTarget == OutputTargetType.M3uFile ? job.M3uOutputPath : null),
                    };

            if (targets.Count == 0)
                errors.Add("No publish targets configured");

            // Archive copies are loaded once for every target that publishes
            // one. Primary targets pay nothing for this query.
            var archiveCopies = await LoadArchiveCopiesAsync(db, matchedTracks, targets, ct);

            resolvedTracks = matchedTracks.Count;
            var targetLabels = new List<string>();

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var built = BuildPlaylist(job, matchedTracks, target, archiveCopies);
                    var playlist = built.Playlist;
                    warnings.AddRange(built.Warnings);
                    if (playlist.Tracks.Count == 0 && matchedTracks.Count > 0)
                    {
                        errors.Add(
                            $"{TargetLabel(target)}: no tracks have a {SourceVariantLabel(target)} copy");
                        continue;
                    }

                    var result = await PublishTargetAsync(target, job, playlist, searchPaths, ct);
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


    /// <summary>One root and the prefix that marks its directory boundary.</summary>
    private static (string Root, string Prefix) RootMatch(string root)
    {
        var normalized = root.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // The filesystem root ("/" or "C:\") trims to nothing or a drive
        // letter: every path below it is inside by definition.
        var prefix = normalized.Length == 0
            ? root[..Math.Min(1, root.Length)]
            : normalized + Path.DirectorySeparatorChar;
        return (normalized, prefix);
    }

    /// <summary>
    /// True when the path sits inside the root at a directory boundary:
    /// "/music/List A" is inside "/music/List A", "/music/List AB" is not.
    /// </summary>
    private static bool IsUnderRoot(string path, string root)
    {
        var (normalized, prefix) = RootMatch(root);
        if (normalized.Length == 0) return path.StartsWith(prefix, StringComparison.Ordinal);
        return path.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folder source filter, translated into one expression so SQLite
    /// does the work. Each root is compared at a directory boundary, the
    /// same rule <see cref="IsUnderRoot"/> uses for the in-memory path.
    /// </summary>
    /// <summary>The columns a sync run reads, so a run never loads lyrics or
    /// cover text it does not use.</summary>
    private static readonly Expression<Func<TrackEntity, TrackEntity>> SyncTrackColumns = t => new TrackEntity
    {
        Id = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        DurationMs = t.DurationMs,
        MelodyId = t.MelodyId,
        MediaType = t.MediaType,
        CurrentPath = t.CurrentPath,
        ArchivePath = t.ArchivePath,
        IsLiked = t.IsLiked,
        DownloadStatus = t.DownloadStatus,
    };

    private static Expression<Func<TrackEntity, bool>> PredicateExpr(IReadOnlyList<string> roots)
    {
        if (roots.Count == 1)
        {
            var (r1, p1) = RootMatch(roots[0]);
            return t => t.CurrentPath != null
                && (t.CurrentPath == r1 || t.CurrentPath.StartsWith(p1));
        }
        if (roots.Count == 2)
        {
            var (r1, p1) = RootMatch(roots[0]);
            var (r2, p2) = RootMatch(roots[1]);
            return t => t.CurrentPath != null
                && (t.CurrentPath == r1 || t.CurrentPath.StartsWith(p1)
                    || t.CurrentPath == r2 || t.CurrentPath.StartsWith(p2));
        }
        if (roots.Count == 3)
        {
            var (r1, p1) = RootMatch(roots[0]);
            var (r2, p2) = RootMatch(roots[1]);
            var (r3, p3) = RootMatch(roots[2]);
            return t => t.CurrentPath != null
                && (t.CurrentPath == r1 || t.CurrentPath.StartsWith(p1)
                    || t.CurrentPath == r2 || t.CurrentPath.StartsWith(p2)
                    || t.CurrentPath == r3 || t.CurrentPath.StartsWith(p3));
        }
        {
            var (r1, p1) = RootMatch(roots[0]);
            var (r2, p2) = RootMatch(roots[1]);
            var (r3, p3) = RootMatch(roots[2]);
            var (r4, p4) = RootMatch(roots[3]);
            return t => t.CurrentPath != null
                && (t.CurrentPath == r1 || t.CurrentPath.StartsWith(p1)
                    || t.CurrentPath == r2 || t.CurrentPath.StartsWith(p2)
                    || t.CurrentPath == r3 || t.CurrentPath.StartsWith(p3)
                    || t.CurrentPath == r4 || t.CurrentPath.StartsWith(p4));
        }
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
    /// Loads the archive copies the targets ask for, keyed by track and
    /// archive profile. The default archive lives in Tracks.ArchivePath,
    /// so only named profiles need this query.
    /// </summary>
    private static async Task<Dictionary<(int TrackId, string ProfileId), string>>
        LoadArchiveCopiesAsync(
            MelodyBridgeDbContext db,
            List<TrackEntity> tracks,
            List<SyncTarget> targets,
            CancellationToken ct)
    {
        var profileIds = targets
            .Select(t => t.SourceArchiveProfileId)
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && !string.Equals(id, SyncSourceVariants.DefaultArchive, StringComparison.Ordinal))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (profileIds.Count == 0 || tracks.Count == 0)
            return new Dictionary<(int, string), string>();

        var trackIds = tracks.Select(t => t.Id).ToList();
        var rows = await db.TrackArchiveCopies.AsNoTracking()
            .Where(copy => trackIds.Contains(copy.TrackId)
                && profileIds.Contains(copy.ProfileId))
            .Select(copy => new { copy.TrackId, copy.ProfileId, copy.Path })
            .ToListAsync(ct);

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Path))
            .ToDictionary(
                row => (row.TrackId, row.ProfileId),
                row => row.Path!);
    }

    /// <summary>
    /// Builds the playlist for one target. The metadata is shared; only the
    /// file location changes when the target publishes an archive copy.
    /// Tracks without the requested copy are left out, never replaced by
    /// the primary file.
    /// </summary>
    private static TargetPlaylist BuildPlaylist(
        SyncJob job,
        List<TrackEntity> tracks,
        SyncTarget target,
        Dictionary<(int TrackId, string ProfileId), string> archiveCopies)
    {
        var playlistTracks = new List<Track>();
        var warnings = new List<string>();
        foreach (var track in tracks)
        {
            var path = SourcePath(track, target, archiveCopies);
            if (string.IsNullOrWhiteSpace(path))
            {
                warnings.Add(
                    $"{track.Title ?? track.CurrentPath ?? "track"}: no {SourceVariantLabel(target)} copy");
                continue;
            }

            playlistTracks.Add(new Track
            {
                Title = track.Title,
                Duration = track.DurationMs is > 0
                    ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
                    : null,
                Artist = track.Artist,
                SongID = !string.IsNullOrEmpty(track.MelodyId)
                    ? new SongID(Platform.Unknown, track.MelodyId)
                    : null,
                MediaType = Enum.TryParse<MediaType>(track.MediaType, true, out var mt)
                    ? mt : MediaType.UNKNOWN,
                CurrentTrackLocation = new FileLocation(path),
                IsLiked = track.IsLiked,
            });
        }

        return new TargetPlaylist(
            new Playlist
            {
                Name = string.IsNullOrWhiteSpace(target.PlaylistName)
                    ? job.Name : target.PlaylistName,
                Tracks = playlistTracks,
            },
            warnings);
    }

    private sealed record TargetPlaylist(Playlist Playlist, List<string> Warnings);

    /// <summary>The file a target publishes: primary, default archive or a
    /// named archive copy.</summary>
    private static string? SourcePath(
        TrackEntity track,
        SyncTarget target,
        Dictionary<(int TrackId, string ProfileId), string> archiveCopies)
    {
        var variant = target.SourceArchiveProfileId;
        if (string.IsNullOrWhiteSpace(variant)) return track.CurrentPath;
        if (string.Equals(variant, SyncSourceVariants.DefaultArchive, StringComparison.Ordinal))
            return track.ArchivePath;
        return archiveCopies.TryGetValue((track.Id, variant), out var path) ? path : null;
    }

    private static string SourceVariantLabel(SyncTarget target)
        => string.IsNullOrWhiteSpace(target.SourceArchiveProfileId)
            ? "primary"
            : string.Equals(target.SourceArchiveProfileId,
                SyncSourceVariants.DefaultArchive, StringComparison.Ordinal)
                ? "default archive"
                : "archive";

    /// <summary>A target with a publish profile owns its rules, even when
    /// they are empty. A target without one keeps the job-level fallback
    /// that direct callers and old rows rely on.</summary>
    private static bool HasTargetProfile(SyncTarget target)
        => !string.IsNullOrWhiteSpace(target.RuleProfileId);

    private static Dictionary<string, string>? PathRemapFor(SyncTarget target, SyncJob job)
    {
        var rules = target.PathRemapRules ?? job.PathRemapRules;
        return rules is { Count: > 0 } ? rules : null;
    }

    private static Dictionary<string, string>? ExtensionRemapFor(SyncTarget target, SyncJob job)
    {
        var rules = target.ExtensionRemapRules ?? job.ExtensionRemapRules;
        return rules is { Count: > 0 } ? rules : null;
    }

    private static bool M3uRelativeFor(SyncTarget target, SyncJob job)
        => HasTargetProfile(target)
            ? target.M3uRelativePaths
            : target.M3uRelativePaths || job.M3uRelativePaths;

    private static bool M3uCrlfFor(SyncTarget target, SyncJob job)
        => HasTargetProfile(target)
            ? target.M3uCrlf
            : target.M3uCrlf || job.M3uCrlf;

    private static bool M3uBackslashesFor(SyncTarget target, SyncJob job)
        => HasTargetProfile(target)
            ? target.M3uBackslashes
            : target.M3uBackslashes || job.M3uBackslashes;

    private static bool M3uBomFor(SyncTarget target, SyncJob job)
        => HasTargetProfile(target)
            ? target.M3uBom
            : target.M3uBom || job.M3uBom;

    /// <summary>
    /// Publishes the playlist to one target. Returns what to record instead
    /// of touching the run's error list, so the caller can survive a target
    /// that throws and still publish the others.
    /// </summary>
    private async Task<TargetRunResult> PublishTargetAsync(
        SyncTarget target, SyncJob job, Playlist playlist,
        IEnumerable<ScanLocation> searchPaths,
        CancellationToken ct)
    {
        if (target.Kind == OutputTargetType.M3uFile)
        {
            if (string.IsNullOrWhiteSpace(target.M3uPath))
                return new TargetRunResult(null, Array.Empty<string>(), "M3U file: no output path configured");
            var m3uPathRemap = PathRemapFor(target, job);
            var m3uExtensionRemap = ExtensionRemapFor(target, job);
            var options = new PlaylistOutputOptions(
                target.M3uPath, M3uRelativeFor(target, job),
                m3uPathRemap, ExtensionRemap: m3uExtensionRemap,
                M3uText: new M3uTextOptions(
                    M3uCrlfFor(target, job),
                    M3uBackslashesFor(target, job),
                    M3uBomFor(target, job)));
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
        var serverOptions = new PlaylistOutputOptions(
            "/playlists/" + playlist.Name + ".m3u", false,
            PathRemapFor(target, job), connection, ExtensionRemapFor(target, job),
            AllowMetadataFallback: target.AllowMetadataFallback
                && string.IsNullOrWhiteSpace(target.SourceArchiveProfileId));
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
