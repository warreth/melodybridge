using System.Globalization;
using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.AppleMusic;

/// <summary>
/// Maps Apple Music playlist and song payloads into MelodyBridge's Core
/// models. The public catalog and the user's library use slightly different
/// shapes; this reads both without guessing.
/// </summary>
public static class AppleMusicMapper
{
    /// <summary>
    /// Maps a playlist response. Accepts a full response (data[0]) or the
    /// playlist object itself.
    /// </summary>
    public static Playlist MapPlaylist(
        JsonElement root, string fallbackId, string canonicalUrl)
    {
        var item = FirstDataItem(root) ?? root;
        var attributes = Prop(item, "attributes");
        var tracks = MapTracks(item);

        var globalId = String(attributes, "playParams", "globalId");
        var itemId = String(item, "id");
        var id = !string.IsNullOrWhiteSpace(globalId)
            ? globalId
            : !string.IsNullOrWhiteSpace(itemId) ? itemId : fallbackId;

        var declared = Int(attributes, "trackCount");
        var playlist = new Playlist
        {
            Id = id,
            Name = String(attributes, "name")?.Trim() ?? "Apple Music playlist",
            Owner = String(attributes, "curatorName")?.Trim(),
            Description = String(attributes, "description", "standard")
                          ?? String(attributes, "editorialNotes", "standard"),
            SourceUrl = String(item, "url") ?? canonicalUrl,
            CoverImageUrl = Artwork(attributes),
            Tracks = tracks,
            TrackCount = tracks.Count,
            Duration = SumDurations(tracks),
        };

        if (declared is { } total && total > tracks.Count)
            playlist.Warning = $"partial fetch: read {tracks.Count} of {total} tracks";

        return playlist;
    }

    /// <summary>Maps every song in a page, skipping music videos and empty rows.</summary>
    public static List<Track> MapTracks(JsonElement root)
    {
        var tracks = new List<Track>();
        var array = TracksArray(root);
        if (array is null) return tracks;

        foreach (var item in array.Value.EnumerateArray())
        {
            if (MapTrack(item) is { } track) tracks.Add(track);
        }
        return tracks;
    }

    /// <summary>Maps one song item, or null when it is not a song or lacks a title.</summary>
    public static Track? MapTrack(JsonElement item)
    {
        var type = String(item, "type") ?? string.Empty;
        if (type.Contains("music-video", StringComparison.OrdinalIgnoreCase)
            || type.Contains("uploaded-video", StringComparison.OrdinalIgnoreCase))
            return null;

        var attributes = Prop(item, "attributes");
        if (attributes is null) return null;

        var title = String(attributes, "name");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var itemId = String(item, "id") ?? string.Empty;
        var catalogId = String(attributes, "playParams", "catalogId");
        var songId = !string.IsNullOrWhiteSpace(catalogId) ? catalogId : itemId;
        var url = String(attributes, "url");

        return new Track
        {
            Title = title.Trim(),
            Artist = String(attributes, "artistName")?.Trim() ?? string.Empty,
            Album = String(attributes, "albumName")?.Trim(),
            AlbumArtist = String(attributes, "albumArtistName")?.Trim(),
            CoverUrl = Artwork(attributes),
            Isrc = String(attributes, "isrc")?.Trim(),
            Duration = Milliseconds(Long(attributes, "durationInMillis")),
            SourcePlatform = Platform.AppleMusic,
            SongID = string.IsNullOrWhiteSpace(songId)
                ? null
                : new SongID(Platform.AppleMusic, songId),
            PlatformSongID = string.IsNullOrWhiteSpace(songId)
                ? null
                : new SongID(Platform.AppleMusic, songId),
            IsLiked = false,
        };
    }

    /// <summary>Maps one library playlist row for the import picker.</summary>
    public static UserPlaylist? MapUserPlaylist(JsonElement item)
    {
        var attributes = Prop(item, "attributes");
        if (attributes is null) return null;

        var libraryId = String(item, "id");
        var globalId = String(attributes, "playParams", "globalId");
        var id = !string.IsNullOrWhiteSpace(globalId) && globalId.StartsWith("pl.", StringComparison.Ordinal)
            ? globalId
            : libraryId;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var name = String(attributes, "name") ?? "Apple Music playlist";
        return new UserPlaylist(
            id,
            name,
            String(attributes, "curatorName"),
            Int(attributes, "trackCount") ?? 0,
            IsLikedSongs: false,
            CoverImageUrl: Artwork(attributes),
            IsOwned: Bool(attributes, "canEdit") ?? false);
    }

    /// <summary>The next page URL from a playlist or listing response.</summary>
    public static string? NextLink(JsonElement root)
    {
        var playlist = FirstDataItem(root);
        if (playlist is { } item
            && TryGet(item, out var next, "relationships", "tracks", "next")
            && next.ValueKind == JsonValueKind.String)
            return next.GetString();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("next", out var rootNext)
            && rootNext.ValueKind == JsonValueKind.String)
            return rootNext.GetString();
        return null;
    }

    /// <summary>The total a library listing reports, when Apple sends one.</summary>
    public static int? Total(JsonElement root)
        => TryGet(root, out var meta, "meta")
           && meta.ValueKind == JsonValueKind.Object
            ? Int(meta, "total")
            : null;

    /// <summary>The data array of a listing response, or an empty list.</summary>
    public static IReadOnlyList<JsonElement> DataArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().ToList();
        return Array.Empty<JsonElement>();
    }

    /// <summary>Apple artwork URL with its size and format placeholders filled in.</summary>
    public static string? Artwork(JsonElement? attributes)
    {
        var url = String(attributes, "artwork", "url");
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url
            .Replace("{w}", "600", StringComparison.Ordinal)
            .Replace("{h}", "600", StringComparison.Ordinal)
            .Replace("{f}", "jpg", StringComparison.Ordinal);
    }

    /// <summary>Reads a string from a nested path. Internal so the page parser can share it.</summary>
    internal static string? String(JsonElement? root, params string[] path)
    {
        if (!TryGet(root, out var value, path)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    /// <summary>Reads an integer from a nested path.</summary>
    internal static int? Int(JsonElement? root, params string[] path)
    {
        if (!TryGet(root, out var value, path)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    /// <summary>Reads a long from a nested path.</summary>
    internal static long? Long(JsonElement? root, params string[] path)
    {
        if (!TryGet(root, out var value, path)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    /// <summary>Reads a boolean from a nested path.</summary>
    internal static bool? Bool(JsonElement? root, params string[] path)
    {
        if (!TryGet(root, out var value, path)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>Reads a nested property, with numeric path segments indexing arrays.</summary>
    internal static bool TryGet(
        JsonElement? root, out JsonElement value, params string[] path)
    {
        value = default;
        if (root is not { } current) return false;

        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength()) return false;
                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
                return false;
        }

        value = current;
        return true;
    }

    /// <summary>Shortcut for a direct child object.</summary>
    internal static JsonElement? Prop(JsonElement? root, params string[] path)
        => TryGet(root, out var value, path) ? value : null;

    private static JsonElement? FirstDataItem(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0)
            return data[0];
        return null;
    }

    private static JsonElement? TracksArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            if (data.GetArrayLength() > 0
                && data[0].TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString()?.Contains("playlists", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Prop(data[0], "relationships", "tracks", "data");
            }
            return data;
        }

        return Prop(root, "relationships", "tracks", "data");
    }

    private static TimeSpan? Milliseconds(long? value)
        => value is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    private static TimeSpan? SumDurations(IEnumerable<Track> tracks)
    {
        var total = TimeSpan.Zero;
        var any = false;
        foreach (var track in tracks)
        {
            if (track.Duration is not { } duration) continue;
            total += duration;
            any = true;
        }
        return any ? total : null;
    }
}
