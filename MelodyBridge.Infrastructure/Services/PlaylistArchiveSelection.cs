using System.Text.Json;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The archive destinations selected for one playlist. A null value is a
/// legacy row: it keeps the old behaviour (default archive plus every
/// enabled profile) so existing playlists do not change on upgrade. An
/// empty list means no archive copies at all.
/// </summary>
public static class PlaylistArchiveSelection
{
    public const string DefaultId = "__default__";

    /// <summary>A legacy per-playlist archive override. Kept selectable until
    /// the playlist is migrated to named profiles.</summary>
    public const string LegacyId = "__legacy__";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Null for a legacy row, otherwise the selected ids.</summary>
    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return ids is null
                ? null
                : ids.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
        }
        catch (JsonException)
        {
            // A corrupt selection must not silently drop every archive copy.
            return null;
        }
    }

    public static string Serialize(IEnumerable<string> ids)
        => JsonSerializer.Serialize(
            ids.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            JsonOptions);

    public static bool IncludesDefault(string? json, bool legacyEnabled)
        => Parse(json) is { } ids ? ids.Contains(DefaultId, StringComparer.Ordinal) : legacyEnabled;
}
