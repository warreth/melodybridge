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
    {
        var raw = await ReadRawAsync(key, ct);
        return raw is null ? fallback : raw is "true" or "1";
    }

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
