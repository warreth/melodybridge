using System.Globalization;
using System.Text.Json;

namespace MelodyBridge.Infrastructure.Jumo;

/// <summary>
/// Small readers for the Jumo response shapes, shared by the search parser
/// and the download descriptor. A track or album id can arrive as a string
/// or a number, and the artist can sit in performer, artists or album.artist.
/// </summary>
internal static class JumoJson
{
    public static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? GetId(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>Primary artist: performer.name, artists[0].name or album.artist.name.</summary>
    public static string? GetPrimaryArtist(JsonElement item)
    {
        if (item.TryGetProperty("performer", out var performer))
        {
            var name = GetString(performer, "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (item.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
        {
            var name = GetString(artists[0], "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (item.TryGetProperty("artist", out var artist))
        {
            var name = GetString(artist, "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (item.TryGetProperty("album", out var album) &&
            album.ValueKind == JsonValueKind.Object &&
            album.TryGetProperty("artist", out var albumArtist))
            return GetString(albumArtist, "name");

        return null;
    }
}

/// <summary>The source identifier Jumo downloads use: <c>jumo:{albumId}:{trackId}</c>.</summary>
internal static class JumoSourceId
{
    public static string Build(string albumId, string trackId) => $"jumo:{albumId}:{trackId}";

    public static bool TryParse(string? sourceUrl, out string albumId, out string trackId)
    {
        albumId = "";
        trackId = "";
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var parts = sourceUrl.Trim().Split(':');
        if (parts.Length != 3 || parts[0] != "jumo") return false;
        if (parts[1].Length == 0 || parts[2].Length == 0) return false;
        albumId = parts[1];
        trackId = parts[2];
        return true;
    }
}
