using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// CRUD and path resolution for named sync rule profiles. A profile is a
/// live bundle: every linked job reads its folder, file-name pattern,
/// M3U format and remap rules at run time.
/// </summary>
public sealed class SyncRuleProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore? _settings;

    /// <summary>Two pages can open at once; the default profiles must not
    /// be created twice. A multi-instance setup would need a database
    /// constraint, which the app does not have.</summary>
    private static readonly SemaphoreSlim EnsureGate = new(1, 1);

    public SyncRuleProfileStore(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore? settings = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    public async Task<List<SyncRuleProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SyncRuleProfiles.AsNoTracking()
            .OrderBy(p => p.Name).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<SyncRuleProfile?> FindAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SyncRuleProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? null : Map(row);
    }

    /// <summary>
    /// Makes sure the app has a usable set of publish profiles: one default
    /// M3U profile when none exists, and one profile per media server
    /// connection. Existing profiles are never changed, so a user's edits
    /// survive every page load.
    /// </summary>
    public async Task<List<SyncRuleProfile>> EnsureDefaultsAsync(
        string defaultM3uFolder,
        IReadOnlyList<MediaServerProfile> serverProfiles,
        CancellationToken ct = default)
    {
        await EnsureGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.SyncRuleProfiles.ToListAsync(ct);
            var changed = false;

            if (!rows.Any(row => string.Equals(
                    row.Kind, nameof(OutputTargetType.M3uFile), StringComparison.OrdinalIgnoreCase)))
            {
                var folder = string.IsNullOrWhiteSpace(defaultM3uFolder)
                    ? "/app/playlists" : defaultM3uFolder.Trim();
                db.SyncRuleProfiles.Add(new SyncRuleProfileEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Default M3U",
                    Kind = nameof(OutputTargetType.M3uFile),
                    M3uOutputFolder = folder,
                    M3uFileNamePattern = "{job}.m3u8",
                    M3uCrlf = true,
                    M3uBom = true,
                    PlaylistNamePattern = "{job}",
                });
                changed = true;
            }

            // Seed one profile per existing connection once. A profile the
            // user deletes stays deleted; creating a new connection does
            // not silently add a destination either. The editor offers the
            // profile button when one is needed.
            var seedServers = _settings is null
                || !await _settings.GetBoolAsync("publish_profiles_seeded", false, ct);
            if (seedServers)
            {
                var known = rows
                    .Where(row => !string.IsNullOrWhiteSpace(row.MediaServerProfileId))
                    .Select(row => (row.Kind, row.MediaServerProfileId!))
                    .ToHashSet();
                foreach (var server in serverProfiles)
                {
                    var kind = MediaServerKinds.OutputTargetFor(server.Kind).ToString();
                    if (!known.Add((kind, server.Id))) continue;
                    db.SyncRuleProfiles.Add(new SyncRuleProfileEntity
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = server.Name,
                        Kind = kind,
                        MediaServerProfileId = server.Id,
                        PlaylistNamePattern = "{job}",
                        M3uOutputFolder = "/app/playlists",
                        M3uFileNamePattern = "{job}.m3u8",
                    });
                    changed = true;
                }
                if (_settings is not null)
                    await _settings.SetAsync("publish_profiles_seeded", "true", ct);
            }

            if (changed) await db.SaveChangesAsync(ct);
            return await GetAllAsync(ct);
        }
        finally
        {
            EnsureGate.Release();
        }
    }

    /// <summary>Creates or updates a profile by id.</summary>
    public async Task SaveAsync(SyncRuleProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("Give the profile a name.");
        if (profile.Kind == OutputTargetType.M3uFile
            && string.IsNullOrWhiteSpace(profile.M3uOutputFolder))
            throw new InvalidOperationException("Enter the M3U output folder.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SyncRuleProfiles.FindAsync(new object[] { profile.Id }, ct);
        if (row is null)
        {
            row = new SyncRuleProfileEntity { Id = profile.Id };
            db.SyncRuleProfiles.Add(row);
        }
        row.Name = profile.Name.Trim();
        row.Kind = profile.Kind.ToString();
        row.MediaServerProfileId = string.IsNullOrWhiteSpace(profile.MediaServerProfileId)
            ? null : profile.MediaServerProfileId.Trim();
        row.PlaylistNamePattern = string.IsNullOrWhiteSpace(profile.PlaylistNamePattern)
            ? "{job}" : profile.PlaylistNamePattern.Trim();
        row.SourceArchiveProfileId = string.IsNullOrWhiteSpace(profile.SourceArchiveProfileId)
            ? null : profile.SourceArchiveProfileId.Trim();
        row.M3uOutputFolder = profile.M3uOutputFolder.Trim();
        row.M3uFileNamePattern = string.IsNullOrWhiteSpace(profile.M3uFileNamePattern)
            ? "{job}.m3u8" : profile.M3uFileNamePattern.Trim();
        row.M3uRelativePaths = profile.M3uRelativePaths;
        row.M3uCrlf = profile.M3uCrlf;
        row.M3uBackslashes = profile.M3uBackslashes;
        row.M3uBom = profile.M3uBom;
        row.PathRemapRules = JsonSerializer.Serialize(profile.PathRemapRules, JsonOptions);
        row.ExtensionRemapRules = JsonSerializer.Serialize(profile.ExtensionRemapRules, JsonOptions);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a profile and removes it from every job that used
    /// it. A legacy job-level link is cleared so the stored values remain
    /// the fallback; a target that pointed at the profile is removed, and
    /// the job is left without that destination until the user adds one.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SyncRuleProfiles.FindAsync(new object[] { id }, ct);
        if (row is null) return false;
        db.SyncRuleProfiles.Remove(row);

        var linked = await db.SyncJobs.ToListAsync(ct);
        foreach (var job in linked)
        {
            if (string.Equals(job.RuleProfileId, id, StringComparison.Ordinal))
                job.RuleProfileId = null;

            var targets = SyncJobMapping.TargetsOf(job).ToList();
            var remaining = targets
                .Where(t => !string.Equals(t.RuleProfileId, id, StringComparison.Ordinal))
                .ToList();
            if (remaining.Count != targets.Count)
                SyncJobMapping.ApplyTargets(job, remaining);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> JobCountAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.SyncJobs.AsNoTracking().ToListAsync(ct);
        return jobs.Count(job =>
            string.Equals(job.RuleProfileId, id, StringComparison.Ordinal)
            || SyncJobMapping.TargetsOf(job)
                .Any(t => string.Equals(t.RuleProfileId, id, StringComparison.Ordinal)));
    }

    /// <summary>The M3U path a linked job writes: profile folder plus the
    /// expanded file name pattern. The pattern cannot escape the folder.</summary>
    public static string ResolveOutputPath(SyncRuleProfile profile, string jobName)
    {
        var folder = string.IsNullOrWhiteSpace(profile.M3uOutputFolder)
            ? "/app/playlists" : profile.M3uOutputFolder.Trim();
        var pattern = string.IsNullOrWhiteSpace(profile.M3uFileNamePattern)
            ? "{job}.m3u8" : profile.M3uFileNamePattern.Trim();
        var expanded = pattern
            .Replace("{job}", jobName, StringComparison.OrdinalIgnoreCase)
            .Replace("{playlist}", jobName, StringComparison.OrdinalIgnoreCase);
        var name = SafeFileNames.Sanitize(expanded, "playlist");
        if (Path.GetExtension(name).Length == 0) name += ".m3u8";
        return Path.Combine(folder.TrimEnd('/', '\\'), name);
    }

    /// <summary>The M3U path for a target, using its per-job unique file
    /// name when quick publish created one. The profile still owns the
    /// folder, so a folder edit reaches the job.</summary>
    public static string ResolveM3uPath(
        SyncRuleProfile profile, string jobName, string? fileNameOverride)
    {
        if (string.IsNullOrWhiteSpace(fileNameOverride))
            return ResolveOutputPath(profile, jobName);
        var folder = string.IsNullOrWhiteSpace(profile.M3uOutputFolder)
            ? "/app/playlists" : profile.M3uOutputFolder.Trim();
        var name = SafeFileNames.Sanitize(fileNameOverride, "playlist");
        if (Path.GetExtension(name).Length == 0) name += ".m3u8";
        return Path.Combine(folder.TrimEnd('/', '\\'), name);
    }

    /// <summary>The target a publish profile creates for one job. An M3U
    /// profile resolves its path; a server profile carries its connection,
    /// playlist name and source variant.</summary>
    public static SyncTarget CreateTarget(
        SyncRuleProfile profile, string jobName, string? m3uPath = null)
    {
        if (profile.Kind == OutputTargetType.M3uFile)
        {
            var path = m3uPath ?? ResolveOutputPath(profile, jobName);
            return new SyncTarget(
                OutputTargetType.M3uFile,
                M3uPath: path,
                RuleProfileId: profile.Id,
                RuleProfileName: profile.Name,
                SourceArchiveProfileId: profile.SourceArchiveProfileId,
                PlaylistName: ResolvePlaylistName(profile, jobName),
                PathRemapRules: profile.PathRemapRules,
                ExtensionRemapRules: profile.ExtensionRemapRules,
                M3uRelativePaths: profile.M3uRelativePaths,
                M3uCrlf: profile.M3uCrlf,
                M3uBackslashes: profile.M3uBackslashes,
                M3uBom: profile.M3uBom,
                M3uFileNameOverride: m3uPath is null ? null : Path.GetFileName(path));
        }

        return new SyncTarget(
            profile.Kind,
            ProfileId: profile.MediaServerProfileId,
            RuleProfileId: profile.Id,
            RuleProfileName: profile.Name,
            SourceArchiveProfileId: profile.SourceArchiveProfileId,
            PlaylistName: ResolvePlaylistName(profile, jobName),
            PathRemapRules: profile.PathRemapRules,
            ExtensionRemapRules: profile.ExtensionRemapRules,
            AllowMetadataFallback: string.IsNullOrWhiteSpace(profile.SourceArchiveProfileId));
    }

    /// <summary>The playlist name a media server target writes: the
    /// profile's pattern with the job name filled in.</summary>
    public static string ResolvePlaylistName(SyncRuleProfile profile, string jobName)
    {
        var pattern = string.IsNullOrWhiteSpace(profile.PlaylistNamePattern)
            ? "{job}" : profile.PlaylistNamePattern.Trim();
        var expanded = pattern
            .Replace("{job}", jobName, StringComparison.OrdinalIgnoreCase)
            .Replace("{playlist}", jobName, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(expanded) ? jobName : expanded;
    }

    /// <summary>Expands a rule profile's optional default file name without
    /// touching the disk, for the editor preview.</summary>
    public static string PreviewFileName(SyncRuleProfile profile, string jobName)
        => Path.GetFileName(ResolveOutputPath(profile, jobName));

    private static SyncRuleProfile Map(SyncRuleProfileEntity row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Kind = Enum.TryParse<OutputTargetType>(row.Kind, true, out var kind)
            ? kind : OutputTargetType.M3uFile,
        MediaServerProfileId = row.MediaServerProfileId,
        PlaylistNamePattern = row.PlaylistNamePattern,
        SourceArchiveProfileId = row.SourceArchiveProfileId,
        M3uOutputFolder = row.M3uOutputFolder,
        M3uFileNamePattern = row.M3uFileNamePattern,
        M3uRelativePaths = row.M3uRelativePaths,
        M3uCrlf = row.M3uCrlf,
        M3uBackslashes = row.M3uBackslashes,
        M3uBom = row.M3uBom,
        PathRemapRules = ReadRules(row.PathRemapRules),
        ExtensionRemapRules = ReadRules(row.ExtensionRemapRules),
    };

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
}
