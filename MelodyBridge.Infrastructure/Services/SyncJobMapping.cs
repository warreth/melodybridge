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
    /// <summary>The domain job for one stored row. A linked rule profile
    /// overrides the M3U path, text format and remap rules.</summary>
    public static SyncJob MapToDomain(SyncJobEntity entity, SyncRuleProfile? ruleProfile = null)
    {
        List<string>? searchPaths = null;
        try { searchPaths = JsonSerializer.Deserialize<List<string>>(entity.SearchLocationPaths ?? "[]"); } catch { }
        var remapRules = new Dictionary<string, string>();
        try { remapRules = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.PathRemapRules ?? "{}") ?? new(); } catch { }
        var extRules = new Dictionary<string, string>();
        try { extRules = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.ExtensionRemapRules ?? "{}") ?? new(); } catch { }

        var targets = TargetsOf(entity);
        var m3uPath = entity.M3uOutputPath;
        var relative = entity.M3uRelativePaths;
        var crlf = entity.M3uCrlf;
        var backslashes = entity.M3uBackslashes;
        var bom = entity.M3uBom;
        if (ruleProfile is not null)
        {
            m3uPath = SyncRuleProfileStore.ResolveOutputPath(ruleProfile, entity.Name);
            targets = targets
                .Select(t => t.Kind == OutputTargetType.M3uFile ? t with { M3uPath = m3uPath } : t)
                .ToList();
            relative = ruleProfile.M3uRelativePaths;
            crlf = ruleProfile.M3uCrlf;
            backslashes = ruleProfile.M3uBackslashes;
            bom = ruleProfile.M3uBom;
            remapRules = ruleProfile.PathRemapRules;
            extRules = ruleProfile.ExtensionRemapRules;
        }

        return new SyncJob
        {
            Id = entity.Id,
            Name = entity.Name ?? "",
            SourceId = entity.SourceId,
            RuleProfileId = ruleProfile?.Id ?? entity.RuleProfileId,
            RuleProfileName = ruleProfile?.Name,
            SearchLocationPaths = searchPaths ?? new(),
            Targets = targets,
            OutputTarget = Enum.TryParse<OutputTargetType>(entity.OutputTarget, true, out var ot) ? ot : OutputTargetType.M3uFile,
            M3uOutputPath = m3uPath,
            M3uRelativePaths = relative,
            M3uCrlf = crlf,
            M3uBackslashes = backslashes,
            M3uBom = bom,
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
    /// Every target of a stored job: the JSON list when the row has one,
    /// otherwise the legacy single target built from OutputTarget and its
    /// fields (jobs from before multi-target support).
    /// </summary>
    public static List<SyncTarget> TargetsOf(SyncJobEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.TargetsJson))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<List<StoredTarget>>(
                    entity.TargetsJson, JsonOptions);
                if (stored is { Count: > 0 })
                {
                    var targets = stored
                        .Where(t => Enum.TryParse<OutputTargetType>(t.Kind, true, out _))
                        .Select(t => new SyncTarget(
                            Enum.Parse<OutputTargetType>(t.Kind, true), t.ProfileId, t.M3uPath))
                        .ToList();
                    if (targets.Count > 0) return targets;
                }
            }
            catch (JsonException)
            {
                // A damaged list falls back to the legacy fields below
                // instead of leaving the job with no target at all.
            }
        }

        var kind = Enum.TryParse<OutputTargetType>(entity.OutputTarget, true, out var legacyKind)
            ? legacyKind
            : OutputTargetType.M3uFile;
        return new List<SyncTarget>
        {
            new(kind,
                kind == OutputTargetType.M3uFile ? null : entity.MediaServerProfileId,
                kind == OutputTargetType.M3uFile ? entity.M3uOutputPath : null),
        };
    }

    /// <summary>
    /// Writes the target list and keeps the legacy single-target columns
    /// mirrored, so older code paths and existing tests keep reading a
    /// sensible first target. The JSON list is the source of truth.
    /// </summary>
    public static void ApplyTargets(SyncJobEntity entity, IEnumerable<SyncTarget> targets)
    {
        var list = targets
            .Where(t => t.Kind == OutputTargetType.M3uFile
                ? !string.IsNullOrWhiteSpace(t.M3uPath)
                : true)
            .DistinctBy(t => (t.Kind, t.ProfileId ?? "", t.M3uPath ?? ""))
            .ToList();

        entity.TargetsJson = JsonSerializer.Serialize(
            list.Select(t => new StoredTarget(t.Kind.ToString(), t.ProfileId, t.M3uPath)),
            JsonOptions);

        var first = list.FirstOrDefault();
        entity.OutputTarget = (first?.Kind ?? OutputTargetType.M3uFile).ToString();
        entity.M3uOutputPath = list.FirstOrDefault(t => t.Kind == OutputTargetType.M3uFile)?.M3uPath;
        entity.MediaServerProfileId = list
            .FirstOrDefault(t => t.Kind != OutputTargetType.M3uFile)?.ProfileId;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record StoredTarget(string Kind, string? ProfileId, string? M3uPath);

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
