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
/// publish profiles the user ticked. Path remaps and per-playlist edits
/// stay on the full Sync jobs page; the jobs this creates open there like
/// any other.
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
    /// Creates one job per distinct playlist id, with one target per
    /// ticked publish profile. The playlist ids and the profiles are
    /// re-read here, so a playlist or profile deleted since the dialog
    /// opened is reported instead of stored as a dangling target.
    /// </summary>
    public static async Task<QuickSyncResult> CreateAsync(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SyncRuleProfileStore ruleProfileStore,
        IEnumerable<(string PlaylistId, string Name)> playlists,
        IReadOnlyList<string> publishProfileIds,
        ScanSchedule schedule,
        CancellationToken ct = default)
    {
        var created = new List<SyncJobEntity>();
        var skipped = new List<QuickSyncSkip>();

        await CreateGate.WaitAsync(ct);
        try
        {
            var wanted = playlists
                .Where(p => !string.IsNullOrWhiteSpace(p.PlaylistId))
                .GroupBy(p => p.PlaylistId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (wanted.Count == 0) return new(created, skipped);

            if (publishProfileIds.Count == 0)
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "no target was selected"));
                return new(created, skipped);
            }

            var profileOrder = publishProfileIds
                .Select((id, index) => (id, index))
                .ToDictionary(entry => entry.id, entry => entry.index, StringComparer.Ordinal);
            var profiles = (await ruleProfileStore.GetAllAsync(ct))
                .Where(profile => publishProfileIds.Contains(profile.Id, StringComparer.Ordinal))
                .OrderBy(profile => profileOrder.GetValueOrDefault(profile.Id, int.MaxValue))
                .ToList();
            if (profiles.Count != publishProfileIds.Count)
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "a selected publish profile no longer exists"));
                return new(created, skipped);
            }

            var (scheduleLabel, cron) = StoredSchedule(schedule);

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
                    Schedule = scheduleLabel,
                    CronExpression = cron,
                    CreatedAt = DateTime.UtcNow,
                    LastRunStatus = "Pending",
                };

                var targets = new List<SyncTarget>();
                foreach (var profile in profiles)
                {
                    if (profile.Kind == OutputTargetType.M3uFile)
                    {
                        var desired = SyncRuleProfileStore.ResolveOutputPath(profile, name);
                        var path = SafeFileNames.UniquePath(desired, usedM3uPaths);
                        usedM3uPaths.Add(SafeFileNames.PathKey(path));
                        // Only a real collision pins a file name. Otherwise
                        // the job keeps following the profile's pattern.
                        var unique = !string.Equals(
                            path, desired, StringComparison.OrdinalIgnoreCase);
                        targets.Add(SyncRuleProfileStore.CreateTarget(
                            profile, name, unique ? path : null));
                    }
                    else
                    {
                        targets.Add(SyncRuleProfileStore.CreateTarget(profile, name));
                    }
                }

                SyncJobMapping.ApplyTargets(entity, targets);
                var m3uProfile = profiles.FirstOrDefault(p => p.Kind == OutputTargetType.M3uFile);
                if (m3uProfile is not null)
                {
                    // Keep the legacy columns in step with the first M3U
                    // profile, so older readers see the same values.
                    entity.RuleProfileId = m3uProfile.Id;
                    entity.M3uRelativePaths = m3uProfile.M3uRelativePaths;
                    entity.M3uCrlf = m3uProfile.M3uCrlf;
                    entity.M3uBackslashes = m3uProfile.M3uBackslashes;
                    entity.M3uBom = m3uProfile.M3uBom;
                    entity.PathRemapRules = JsonSerializer.Serialize(m3uProfile.PathRemapRules);
                    entity.ExtensionRemapRules = JsonSerializer.Serialize(m3uProfile.ExtensionRemapRules);
                }

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
    /// Compatibility path for callers that still pass media server
    /// profiles and the old M3U options. It resolves or creates the
    /// matching publish profiles, then uses the same create path as the
    /// profile-based dialog.
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
        options ??= QuickSyncOptions.Default;
        var store = ruleProfileStore ?? new SyncRuleProfileStore(dbFactory);
        var wanted = playlists.ToList();

        var serverProfiles = (await profileStore.GetAllAsync(ct))
            .Where(profile => profileIds.Contains(profile.Id, StringComparer.Ordinal))
            .ToList();
        if (serverProfiles.Count != profileIds.Count)
        {
            return new(Array.Empty<SyncJobEntity>(),
                wanted.Select(p => new QuickSyncSkip(
                    p.Name, "a selected media server profile no longer exists")).ToList());
        }

        var allProfiles = await store.EnsureDefaultsAsync(
            options.M3uDirectory ?? "/app/playlists", serverProfiles, ct);
        var publishIds = new List<string>();

        if (options.M3uEnabled)
        {
            SyncRuleProfile? m3u = null;
            if (!string.IsNullOrWhiteSpace(options.RuleProfileId))
            {
                m3u = await store.FindAsync(options.RuleProfileId, ct);
                if (m3u is null)
                {
                    return new(Array.Empty<SyncJobEntity>(),
                        wanted.Select(p => new QuickSyncSkip(
                            p.Name, "the selected rule profile no longer exists")).ToList());
                }
            }
            else if (!string.IsNullOrWhiteSpace(options.M3uDirectory))
            {
                m3u = new SyncRuleProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Quick publish M3U",
                    Kind = OutputTargetType.M3uFile,
                    M3uOutputFolder = options.M3uDirectory!,
                    M3uFileNamePattern = "{job}.m3u8",
                    M3uCrlf = true,
                    M3uBom = true,
                };
                await store.SaveAsync(m3u, ct);
            }

            if (m3u is null)
            {
                return new(Array.Empty<SyncJobEntity>(),
                    wanted.Select(p => new QuickSyncSkip(
                        p.Name, "the M3U output folder is not set")).ToList());
            }
            publishIds.Add(m3u.Id);
        }

        foreach (var server in serverProfiles)
        {
            var kind = MediaServerKinds.OutputTargetFor(server.Kind);
            var profile = allProfiles.FirstOrDefault(p => p.Kind == kind
                && string.Equals(p.MediaServerProfileId, server.Id, StringComparison.Ordinal));
            if (profile is not null) publishIds.Add(profile.Id);
        }

        return await CreateAsync(
            dbFactory, store, wanted, publishIds, options.Schedule, ct);
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
