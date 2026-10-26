using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
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

    public SyncRuleProfileStore(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
        => _dbFactory = dbFactory;

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

    /// <summary>Creates or updates a profile by id.</summary>
    public async Task SaveAsync(SyncRuleProfile profile, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SyncRuleProfiles.FindAsync(new object[] { profile.Id }, ct);
        if (row is null)
        {
            row = new SyncRuleProfileEntity { Id = profile.Id };
            db.SyncRuleProfiles.Add(row);
        }
        row.Name = profile.Name.Trim();
        row.M3uOutputFolder = profile.M3uOutputFolder.Trim();
        row.M3uFileNamePattern = profile.M3uFileNamePattern.Trim();
        row.M3uRelativePaths = profile.M3uRelativePaths;
        row.M3uCrlf = profile.M3uCrlf;
        row.M3uBackslashes = profile.M3uBackslashes;
        row.M3uBom = profile.M3uBom;
        row.PathRemapRules = JsonSerializer.Serialize(profile.PathRemapRules, JsonOptions);
        row.ExtensionRemapRules = JsonSerializer.Serialize(profile.ExtensionRemapRules, JsonOptions);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a profile and unlinks its jobs. The jobs keep their
    /// stored custom values as the fallback.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SyncRuleProfiles.FindAsync(new object[] { id }, ct);
        if (row is null) return false;
        db.SyncRuleProfiles.Remove(row);
        var linked = await db.SyncJobs.Where(j => j.RuleProfileId == id).ToListAsync(ct);
        foreach (var job in linked) job.RuleProfileId = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> JobCountAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SyncJobs.CountAsync(j => j.RuleProfileId == id, ct);
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

    /// <summary>Expands a rule profile's optional default file name without
    /// touching the disk, for the editor preview.</summary>
    public static string PreviewFileName(SyncRuleProfile profile, string jobName)
        => Path.GetFileName(ResolveOutputPath(profile, jobName));

    private static SyncRuleProfile Map(SyncRuleProfileEntity row) => new()
    {
        Id = row.Id,
        Name = row.Name,
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
