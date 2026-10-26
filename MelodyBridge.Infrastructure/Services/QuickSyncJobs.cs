using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One playlist the quick publish flow did not turn into a job, with the
/// reason, so the page can report it instead of silently dropping rows.
/// </summary>
public sealed record QuickSyncSkip(string Playlist, string Reason);

/// <summary>What one quick publish created, and what it had to skip.</summary>
public sealed record QuickSyncResult(
    IReadOnlyList<SyncJobEntity> Created,
    IReadOnlyList<QuickSyncSkip> Skipped);

/// <summary>
/// The choices the quick publish dialog applies to every playlist it
/// turns into a job. The defaults keep the original behaviour: manual
/// schedule, media server profiles only.
/// </summary>
public sealed record QuickSyncOptions(
    ScanSchedule Schedule,
    bool M3uEnabled = false,
    string? M3uDirectory = null,
    string? RuleProfileId = null)
{
    public static QuickSyncOptions Default { get; } =
        new(ScanSchedule.Manual);
}

/// <summary>
/// The simplified path from a saved playlist to a publishing job: one
/// job per playlist, named after the playlist, with the schedule and
/// targets the user ticked. Path remaps and per-playlist edits stay on
/// the full Sync jobs page; the jobs this creates open there like any
/// other.
/// </summary>
public static class QuickSyncJobs
{
    /// <summary>
    /// One create at a time: two browser tabs submitting the same list must
    /// not both pass the "playlist has no job yet" check. The app is one
    /// process; a multi-instance setup would need a database constraint,
    /// which is deliberately not added because the full wizard may create
    /// several jobs for one playlist.
    /// </summary>
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    /// <summary>
    /// Playlist ids that already have a sync job. The quick publish list
    /// hides those, so the list does not offer the same playlist twice.
    /// </summary>
    public static async Task<HashSet<string>> PlaylistsWithJobAsync(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.SyncJobs
            .Where(j => j.SourceId != null)
            .Select(j => j.SourceId!)
            .ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates one job per distinct playlist id: the playlist as its
    /// source, the requested schedule, and one target per ticked profile,
    /// plus an M3U file when requested. The playlist ids and the profiles
    /// are re-read here, so a playlist or profile deleted since the dialog
    /// opened is reported instead of stored as a dangling target. Returns
    /// the created jobs and every skipped playlist with its reason, in
    /// playlist order.
    /// </summary>
    public static async Task<QuickSyncResult> CreateAsync(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        MediaServerProfileStore profileStore,
        IEnumerable<(string PlaylistId, string Name)> playlists,
        IReadOnlyList<string> profileIds,
        QuickSyncOptions? options = null,
        SyncRuleProfileStore? ruleProfileStore = null,
        CancellationToken ct = default)
    {
        var created = new List<SyncJobEntity>();
        var skipped = new List<QuickSyncSkip>();
        options ??= QuickSyncOptions.Default;

        await CreateGate.WaitAsync(ct);
        try
        {
            var wanted = playlists
                .Where(p => !string.IsNullOrWhiteSpace(p.PlaylistId))
                .GroupBy(p => p.PlaylistId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (wanted.Count == 0) return new(created, skipped);

            if (!options.M3uEnabled && profileIds.Count == 0)
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "no target was selected"));
                return new(created, skipped);
            }
            SyncRuleProfile? ruleProfile = null;
            if (options.M3uEnabled && !string.IsNullOrWhiteSpace(options.RuleProfileId))
            {
                if (ruleProfileStore is not null)
                    ruleProfile = await ruleProfileStore.FindAsync(options.RuleProfileId, ct);
                if (ruleProfile is null)
                {
                    foreach (var (_, name) in wanted)
                        skipped.Add(new(name, "the selected rule profile no longer exists"));
                    return new(created, skipped);
                }
            }
            if (options.M3uEnabled && ruleProfile is null
                && string.IsNullOrWhiteSpace(options.M3uDirectory))
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "the M3U output folder is not set"));
                return new(created, skipped);
            }

            var existingProfiles = (await profileStore.GetAllAsync(ct))
                .Where(p => profileIds.Contains(p.Id, StringComparer.Ordinal))
                .ToList();
            var missingProfile = existingProfiles.Count != profileIds.Count;
            if (missingProfile)
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "a selected media server profile no longer exists"));
                return new(created, skipped);
            }

            var (schedule, cron) = StoredSchedule(options.Schedule);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var wantedIds = wanted.Select(p => p.PlaylistId).ToList();
            var existingPlaylistIds = await db.Playlists
                .Where(p => wantedIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct);
            var existingPlaylists = existingPlaylistIds.ToHashSet(StringComparer.Ordinal);

            // Read every job once: the source ids prevent a second job for
            // a playlist, and the M3U paths let a new job choose a unique
            // file even when names collide with each other or an old job.
            var existingJobs = await db.SyncJobs.ToListAsync(ct);
            var takenSet = existingJobs
                .Where(j => j.SourceId != null)
                .Select(j => j.SourceId!)
                .ToHashSet(StringComparer.Ordinal);
            var usedM3uPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existingJob in existingJobs)
            {
                foreach (var target in SyncJobMapping.TargetsOf(existingJob))
                {
                    if (target.Kind == OutputTargetType.M3uFile
                        && !string.IsNullOrWhiteSpace(target.M3uPath))
                    {
                        usedM3uPaths.Add(SafeFileNames.PathKey(target.M3uPath));
                    }
                }
            }

            foreach (var (playlistId, name) in wanted)
            {
                if (!existingPlaylists.Contains(playlistId))
                {
                    skipped.Add(new(name, "the playlist no longer exists"));
                    continue;
                }
                if (takenSet.Contains(playlistId))
                {
                    skipped.Add(new(name, "it already has a sync job"));
                    continue;
                }

                var entity = new SyncJobEntity
                {
                    Name = name,
                    SourceId = playlistId,
                    Schedule = schedule,
                    CronExpression = cron,
                    CreatedAt = DateTime.UtcNow,
                    LastRunStatus = "Pending",
                };
                var targets = new List<SyncTarget>();
                if (options.M3uEnabled)
                {
                    string m3uPath;
                    if (ruleProfile is not null)
                    {
                        // A linked job follows the profile live. Keep a copy
                        // of its format and rules on the row too, so deleting
                        // the profile leaves the job with what it had.
                        entity.RuleProfileId = ruleProfile.Id;
                        entity.M3uRelativePaths = ruleProfile.M3uRelativePaths;
                        entity.M3uCrlf = ruleProfile.M3uCrlf;
                        entity.M3uBackslashes = ruleProfile.M3uBackslashes;
                        entity.M3uBom = ruleProfile.M3uBom;
                        entity.PathRemapRules = JsonSerializer.Serialize(ruleProfile.PathRemapRules);
                        entity.ExtensionRemapRules = JsonSerializer.Serialize(ruleProfile.ExtensionRemapRules);
                        m3uPath = SafeFileNames.UniquePath(
                            SyncRuleProfileStore.ResolveOutputPath(ruleProfile, name), usedM3uPaths);
                    }
                    else
                    {
                        m3uPath = SafeFileNames.UniqueFile(
                            options.M3uDirectory!, name, ".m3u8", usedM3uPaths);
                        // Quick publish has no format step: use the widest text
                        // defaults, and let the job's editor switch to Plain.
                        entity.M3uCrlf = true;
                        entity.M3uBom = true;
                    }
                    usedM3uPaths.Add(SafeFileNames.PathKey(m3uPath));
                    targets.Add(new SyncTarget(OutputTargetType.M3uFile, M3uPath: m3uPath));
                }
                targets.AddRange(existingProfiles.Select(p =>
                    new SyncTarget(MediaServerKinds.OutputTargetFor(p.Kind), ProfileId: p.Id)));
                SyncJobMapping.ApplyTargets(entity, targets);
                db.SyncJobs.Add(entity);
                takenSet.Add(playlistId);
                created.Add(entity);
            }

            if (created.Count > 0) await db.SaveChangesAsync(ct);
        }
        finally
        {
            CreateGate.Release();
        }

        return new(created, skipped);
    }

    /// <summary>
    /// The stored schedule pair for a job row: named presets keep their
    /// label (the scheduler resolves them from the shared preset table),
    /// a custom expression is stored as Cron plus its text.
    /// </summary>
    private static (string Schedule, string? Cron) StoredSchedule(ScanSchedule schedule)
        => schedule.Mode switch
        {
            ScanScheduleMode.Cron when schedule.NamedPreset is { } preset => (preset, null),
            ScanScheduleMode.Cron => (nameof(SyncJobSchedule.Cron), schedule.Cron),
            _ => (nameof(SyncJobSchedule.Manual), null),
        };
}
