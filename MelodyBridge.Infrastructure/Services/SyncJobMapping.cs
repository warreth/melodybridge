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
        => MapToDomain(entity,
            ruleProfile is null ? null : new[] { ruleProfile },
            useSingleAsLegacy: true);

    /// <summary>The domain job for one stored row, resolving every target's
    /// own publish profile. A target without a profile keeps the legacy
    /// job-level fallback, so old jobs run unchanged until edited.</summary>
    public static SyncJob MapToDomain(
        SyncJobEntity entity, IReadOnlyList<SyncRuleProfile>? ruleProfiles)
        => MapToDomain(entity, ruleProfiles, useSingleAsLegacy: false);

    private static SyncJob MapToDomain(
        SyncJobEntity entity,
        IReadOnlyList<SyncRuleProfile>? ruleProfiles,
        bool useSingleAsLegacy)
    {
        List<string>? searchPaths = null;
        try { searchPaths = JsonSerializer.Deserialize<List<string>>(entity.SearchLocationPaths ?? "[]"); } catch { }

        var profiles = ruleProfiles?
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
            ?? new Dictionary<string, SyncRuleProfile>(StringComparer.Ordinal);

        SyncRuleProfile? legacyProfile = null;
        if (!string.IsNullOrWhiteSpace(entity.RuleProfileId))
            profiles.TryGetValue(entity.RuleProfileId, out legacyProfile);
        // The single-profile overload is the legacy path: callers hand the
        // job's profile directly without mirroring it onto the row. The
        // list overload never guesses, so the editor can show a legacy
        // target and ask for a profile instead of silently adopting one.
        if (useSingleAsLegacy && legacyProfile is null && ruleProfiles is { Count: 1 })
            legacyProfile = ruleProfiles[0];

        var targets = TargetsOf(entity)
            .Select(target => ResolveTarget(target, entity, profiles, legacyProfile))
            .ToList();

        // The job-level columns mirror the first M3U target, or the first
        // target when the job has no M3U output. The runner reads the
        // target itself; these stay for older readers and the preview.
        var m3uTarget = targets.FirstOrDefault(t => t.Kind == OutputTargetType.M3uFile);
        var first = m3uTarget ?? targets.FirstOrDefault();
        var remapRules = first?.PathRemapRules ?? new Dictionary<string, string>();
        var extRules = first?.ExtensionRemapRules ?? new Dictionary<string, string>();

        return new SyncJob
        {
            Id = entity.Id,
            Name = entity.Name ?? "",
            SourceId = entity.SourceId,
            RuleProfileId = legacyProfile?.Id ?? entity.RuleProfileId,
            RuleProfileName = legacyProfile?.Name,
            SearchLocationPaths = searchPaths ?? new(),
            Targets = targets,
            HasExplicitTargets = !string.IsNullOrWhiteSpace(entity.TargetsJson),
            OutputTarget = Enum.TryParse<OutputTargetType>(entity.OutputTarget, true, out var ot) ? ot : OutputTargetType.M3uFile,
            M3uOutputPath = m3uTarget?.M3uPath,
            M3uRelativePaths = m3uTarget?.M3uRelativePaths ?? false,
            M3uCrlf = m3uTarget?.M3uCrlf ?? false,
            M3uBackslashes = m3uTarget?.M3uBackslashes ?? false,
            M3uBom = m3uTarget?.M3uBom ?? false,
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
    /// Resolves one stored target against its publish profile. A profile
    /// wins over the target's stored kind and connection; the target keeps
    /// the profile id so the UI can show which profile owns it.
    /// </summary>
    private static SyncTarget ResolveTarget(
        SyncTarget target,
        SyncJobEntity entity,
        IReadOnlyDictionary<string, SyncRuleProfile> profiles,
        SyncRuleProfile? legacyProfile)
    {
        SyncRuleProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(target.RuleProfileId))
            profiles.TryGetValue(target.RuleProfileId, out profile);

        // A legacy M3U target is still owned by the job-level profile.
        if (profile is null && target.Kind == OutputTargetType.M3uFile
            && string.IsNullOrWhiteSpace(target.RuleProfileId))
        {
            profile = legacyProfile;
        }

        if (profile is null)
        {
            // Legacy target: the job's own values are the fallback.
            return target with
            {
                PathRemapRules = ReadRules(entity.PathRemapRules),
                ExtensionRemapRules = ReadRules(entity.ExtensionRemapRules),
                M3uRelativePaths = target.M3uRelativePaths || entity.M3uRelativePaths,
                M3uCrlf = target.M3uCrlf || entity.M3uCrlf,
                M3uBackslashes = target.M3uBackslashes || entity.M3uBackslashes,
                M3uBom = target.M3uBom || entity.M3uBom,
                AllowMetadataFallback = true,
            };
        }

        var kind = profile.Kind;
        var m3uPath = target.M3uPath;
        var fileNameOverride = target.M3uFileNameOverride;
        if (kind == OutputTargetType.M3uFile)
        {
            // Quick publish stores a unique file name per job. The profile
            // still owns the folder, so a folder edit reaches the job. A
            // row written before the override field existed still carries
            // the numbered suffix in its stored path; recover it here.
            if (string.IsNullOrWhiteSpace(fileNameOverride)
                && target.M3uPath is { Length: > 0 } storedPath
                && LooksLikeCollisionSuffix(Path.GetFileName(storedPath)))
            {
                fileNameOverride = Path.GetFileName(storedPath);
            }
            m3uPath = SyncRuleProfileStore.ResolveM3uPath(
                profile, entity.Name ?? "", fileNameOverride);
        }

        return target with
        {
            Kind = kind,
            ProfileId = kind == OutputTargetType.M3uFile
                ? null
                : profile.MediaServerProfileId ?? target.ProfileId,
            M3uPath = m3uPath,
            RuleProfileId = profile.Id,
            RuleProfileName = profile.Name,
            SourceArchiveProfileId = profile.SourceArchiveProfileId,
            PlaylistName = SyncRuleProfileStore.ResolvePlaylistName(profile, entity.Name ?? ""),
            PathRemapRules = profile.PathRemapRules,
            ExtensionRemapRules = profile.ExtensionRemapRules,
            M3uRelativePaths = profile.M3uRelativePaths,
            M3uCrlf = profile.M3uCrlf,
            M3uBackslashes = profile.M3uBackslashes,
            M3uBom = profile.M3uBom,
            M3uFileNameOverride = fileNameOverride,
            AllowMetadataFallback = string.IsNullOrWhiteSpace(profile.SourceArchiveProfileId),
        };
    }

    /// <summary>True for the numbered suffix quick publish adds when two
    /// playlist names sanitize to the same file, for example "Mix (2).m3u8".</summary>
    private static bool LooksLikeCollisionSuffix(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !stem.EndsWith(')')) return false;
        var number = stem[(open + 2)..^1];
        return int.TryParse(number, out var suffix) && suffix >= 2;
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
                if (stored is not null)
                {
                    // An empty list is explicit: the user removed every
                    // target. Only a null or damaged list is legacy.
                    return stored
                        .Where(t => Enum.TryParse<OutputTargetType>(t.Kind, true, out _))
                        .Select(t => new SyncTarget(
                            Enum.Parse<OutputTargetType>(t.Kind, true),
                            t.ProfileId,
                            t.M3uPath,
                            t.RuleProfileId,
                            SourceArchiveProfileId: t.SourceArchiveProfileId,
                            PlaylistName: t.PlaylistName,
                            M3uFileNameOverride: t.M3uFileNameOverride))
                        .ToList();
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
            .DistinctBy(t => (t.Kind, t.ProfileId ?? "", t.M3uPath ?? "",
                t.RuleProfileId ?? "", t.SourceArchiveProfileId ?? "",
                t.PlaylistName ?? "", t.M3uFileNameOverride ?? ""))
            .ToList();

        entity.TargetsJson = JsonSerializer.Serialize(
            list.Select(t => new StoredTarget(
                t.Kind.ToString(), t.ProfileId, t.M3uPath, t.RuleProfileId,
                t.SourceArchiveProfileId, t.PlaylistName, t.M3uFileNameOverride)),
            JsonOptions);

        var first = list.FirstOrDefault();
        entity.OutputTarget = (first?.Kind ?? OutputTargetType.M3uFile).ToString();
        entity.M3uOutputPath = list.FirstOrDefault(t => t.Kind == OutputTargetType.M3uFile)?.M3uPath;
        entity.MediaServerProfileId = list
            .FirstOrDefault(t => t.Kind != OutputTargetType.M3uFile)?.ProfileId;
    }

    private static Dictionary<string, string> ReadRules(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}", JsonOptions)
                ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record StoredTarget(
        string Kind,
        string? ProfileId,
        string? M3uPath,
        string? RuleProfileId = null,
        string? SourceArchiveProfileId = null,
        string? PlaylistName = null,
        string? M3uFileNameOverride = null);

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
