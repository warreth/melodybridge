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
/// The simplified path from a saved playlist to a publishing job: one
/// Manual job per playlist, named after the playlist, pointing at the
/// media server profiles the user ticked. Everything else (schedules,
/// path remaps, M3U output, single-target edits) stays on the full Sync
/// jobs page; the jobs this creates open there like any other.
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
    /// Creates one job per distinct playlist id: Manual schedule, the
    /// playlist as its source, and one target per ticked profile. The
    /// playlist ids and the profiles are re-read here, so a playlist or
    /// profile deleted since the dialog opened is reported instead of
    /// stored as a dangling target. Returns the created jobs and every
    /// skipped playlist with its reason, in playlist order.
    /// </summary>
    public static async Task<QuickSyncResult> CreateAsync(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        MediaServerProfileStore profileStore,
        IEnumerable<(string PlaylistId, string Name)> playlists,
        IReadOnlyList<string> profileIds,
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

            var existingProfiles = (await profileStore.GetAllAsync(ct))
                .Where(p => profileIds.Contains(p.Id, StringComparer.Ordinal))
                .ToList();
            if (profileIds.Count == 0)
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "no media server profile was selected"));
                return new(created, skipped);
            }
            var missingProfile = existingProfiles.Count != profileIds.Count;
            if (missingProfile)
            {
                foreach (var (_, name) in wanted)
                    skipped.Add(new(name, "a selected media server profile no longer exists"));
                return new(created, skipped);
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var wantedIds = wanted.Select(p => p.PlaylistId).ToList();
            var existingPlaylistIds = await db.Playlists
                .Where(p => wantedIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct);
            var existingPlaylists = existingPlaylistIds.ToHashSet(StringComparer.Ordinal);

            var taken = await db.SyncJobs
                .Where(j => j.SourceId != null)
                .Select(j => j.SourceId!)
                .ToListAsync(ct);
            var takenSet = taken.ToHashSet(StringComparer.Ordinal);

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
                    Schedule = nameof(SyncJobSchedule.Manual),
                    CreatedAt = DateTime.UtcNow,
                    LastRunStatus = "Pending",
                };
                SyncJobMapping.ApplyTargets(entity, existingProfiles.Select(p =>
                    new SyncTarget(MediaServerKinds.OutputTargetFor(p.Kind), ProfileId: p.Id)));
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
}
