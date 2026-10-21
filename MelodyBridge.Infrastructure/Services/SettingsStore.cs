using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Tiny typed wrapper over the DownloaderSettings key/value table: the one
/// place that reads and writes app-level settings (Settings page, intro
/// flag, advanced UI toggles). Database rows, no env vars, no restart.
/// </summary>
public class SettingsStore
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;

    public SettingsStore(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
        => _dbFactory = dbFactory;

    /// <summary>Current value of a key, or the fallback when the row is missing or empty.</summary>
    public async Task<string> GetAsync(string key, string fallback, CancellationToken ct = default)
        => await ReadRawAsync(key, ct) ?? fallback;

    /// <summary>Current value of a boolean key ("true"/"1" = on).</summary>
    public async Task<bool> GetBoolAsync(string key, bool fallback = false, CancellationToken ct = default)
        => ParseBool(await ReadRawAsync(key, ct), fallback);

    /// <summary>
    /// Reads several keys in one query. A page that needs five settings
    /// should not open five contexts and five connections on every load.
    /// Missing and empty rows are absent from the result, so callers pass a
    /// fallback through <see cref="GetValueOrDefault{T}"/> style lookups.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        IEnumerable<string> keys, CancellationToken ct = default)
    {
        var wanted = keys.Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.DownloaderSettings.AsNoTracking()
            .Where(s => wanted.Contains(s.Key))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Value))
                values[row.Key] = row.Value;
        }
        return values;
    }

    /// <summary>The boolean reading used by both the single and bulk lookups.</summary>
    public static bool ParseBool(string? raw, bool fallback)
        => raw is null ? fallback : raw is "true" or "1";

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DownloaderSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = key, Value = value ?? "" });
        else
            row.Value = value ?? "";
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> ReadRawAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var value = await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
