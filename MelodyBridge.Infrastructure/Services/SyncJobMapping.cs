using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Maps a stored sync job row onto the domain model the runner takes.
/// Shared by the Sync jobs page (edit/run) and the scheduler so the two
/// can never drift apart.
/// </summary>
public static class SyncJobMapping
{
    /// <summary>The domain job for one stored row.</summary>
    public static SyncJob MapToDomain(SyncJobEntity entity)
    {
        List<string>? searchPaths = null;
        try { searchPaths = JsonSerializer.Deserialize<List<string>>(entity.SearchLocationPaths ?? "[]"); } catch { }
        var remapRules = new Dictionary<string, string>();
        try { remapRules = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.PathRemapRules ?? "{}") ?? new(); } catch { }
        var extRules = new Dictionary<string, string>();
        try { extRules = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.ExtensionRemapRules ?? "{}") ?? new(); } catch { }

        return new SyncJob
        {
            Id = entity.Id,
            Name = entity.Name ?? "",
            SourceId = entity.SourceId,
            SearchLocationPaths = searchPaths ?? new(),
            OutputTarget = Enum.TryParse<OutputTargetType>(entity.OutputTarget, true, out var ot) ? ot : OutputTargetType.M3uFile,
            M3uOutputPath = entity.M3uOutputPath,
            JellyfinServerUrl = entity.JellyfinServerUrl,
            JellyfinApiKey = entity.JellyfinApiKey,
            JellyfinUserId = entity.JellyfinUserId,
            MediaServerProfileId = entity.MediaServerProfileId,
            PathRemapRules = remapRules,
            ExtensionRemapRules = extRules,
            Schedule = Enum.TryParse<SyncJobSchedule>(entity.Schedule, true, out var s) ? s : SyncJobSchedule.Manual,
            CronExpression = entity.CronExpression,
            LastRunAt = entity.LastRunAt,
            LastRunSummary = entity.LastRunSummary,
        };
    }

    /// <summary>
    /// The schedule a job runs on: the named presets resolve to their cron
    /// (all scheduling is cron under the hood), a custom job parses its
    /// expression, and anything unknown stays manual.
    /// </summary>
    public static ScanSchedule ScheduleOf(SyncJobEntity entity)
    {
        if (string.Equals(entity.Schedule, nameof(SyncJobSchedule.Cron), StringComparison.OrdinalIgnoreCase))
            return ScanSchedule.Parse(entity.CronExpression);

        var preset = ScanSchedule.NamedPresets.FirstOrDefault(p =>
            string.Equals(p.Label, entity.Schedule, StringComparison.OrdinalIgnoreCase));
        return preset.Cron is null ? ScanSchedule.Manual : ScanSchedule.Parse(preset.Cron);
    }
}
