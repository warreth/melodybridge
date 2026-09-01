using System.Text.Json;
using System.Text.Json.Serialization;
using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// A named media-server connection (Jellyfin today, Plex when it lands)
/// stored in the settings table so several sync jobs can share it.
/// Mutable class on purpose: the settings form binds directly to it.
/// </summary>
public sealed class MediaServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Kind { get; set; } = "Jellyfin";

    /// <summary>Editable copy so Cancel can drop unsaved changes.</summary>
    public MediaServerProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Kind = Kind,
    };
}

/// <summary>
/// CRUD over the media_server_profiles settings key. One JSON list is the
/// whole storage: no schema change, no migration, nothing to break a
/// concurrent session's migrations.
/// </summary>
public sealed class MediaServerProfileStore(SettingsStore settings)
{
    private const string StorageKey = "media_server_profiles";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<MediaServerProfile>> GetAllAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(StorageKey, "[]", ct);
        try
        {
            return JsonSerializer.Deserialize<List<MediaServerProfile>>(raw, Json) ?? [];
        }
        catch
        {
            // A corrupted blob must never take the settings page down.
            return [];
        }
    }

    public async Task<MediaServerProfile?> FindAsync(string id, CancellationToken ct = default)
        => (await GetAllAsync(ct)).FirstOrDefault(p => p.Id == id);

    /// <summary>Inserts or updates by Id. Returns the stored profile.</summary>
    public async Task<MediaServerProfile> SaveAsync(MediaServerProfile profile, CancellationToken ct = default)
    {
        var all = (await GetAllAsync(ct)).ToList();
        var existing = all.FindIndex(p => p.Id == profile.Id);
        if (existing >= 0) all[existing] = profile;
        else all.Add(profile);
        await WriteAsync(all, ct);
        return profile;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var all = (await GetAllAsync(ct)).ToList();
        var removed = all.RemoveAll(p => p.Id == id);
        if (removed == 0) return false;
        await WriteAsync(all, ct);
        return true;
    }

    private Task WriteAsync(List<MediaServerProfile> profiles, CancellationToken ct)
        => settings.SetAsync(StorageKey, JsonSerializer.Serialize(profiles, Json), ct);
}
