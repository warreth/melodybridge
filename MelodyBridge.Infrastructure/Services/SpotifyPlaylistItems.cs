using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Maps one api.spotify.com playlist item into a domain Track.
///
/// Spotify renamed the field that carries the track inside a playlist
/// item from "track" to "item" (the old name still comes back on some
/// responses but is documented as deprecated). Both shapes are accepted
/// here so old and new responses parse the same.
///
/// Shared by the account path (logged in, private playlists work) and
/// the public embed-token path, so the two never drift apart.
/// </summary>
internal static class SpotifyPlaylistItems
{
    public static Track? Parse(JsonElement item)
    {
        // New shape first, deprecated "track" as the fallback.
        if (!item.TryGetProperty("item", out var track) || track.ValueKind != JsonValueKind.Object)
        {
            if (!item.TryGetProperty("track", out track) || track.ValueKind != JsonValueKind.Object)
                return null;
        }

        if (!track.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            return null;

        var id = idProp.GetString();
        if (string.IsNullOrEmpty(id)) return null;

        // Podcasts live in playlists too; they are not downloadable music.
        if (track.TryGetProperty("episode", out var episode) && episode.ValueKind == JsonValueKind.True)
            return null;

        // The item carries a full track object, external_ids included, so
        // the ISRC needs no second lookup.
        var isrc = (string?)null;
        if (track.TryGetProperty("external_ids", out var externalIds)
            && externalIds.ValueKind == JsonValueKind.Object
            && externalIds.TryGetProperty("isrc", out var isrcProp)
            && isrcProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(isrcProp.GetString()))
            isrc = isrcProp.GetString();

        long? durationMs = track.TryGetProperty("duration_ms", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt64()
            : null;

        var artists = track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artistArray.EnumerateArray()
                .Where(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                .Select(a => a.GetProperty("name").GetString()))
            : null;

        // The album object travels with the simplified track: its name, the
        // release credit and the cover art, all of which land on the file
        // tags after the download.
        var album = (string?)null;
        var albumArtist = (string?)null;
        var coverUrl = (string?)null;
        if (track.TryGetProperty("album", out var albumObj) && albumObj.ValueKind == JsonValueKind.Object)
        {
            if (albumObj.TryGetProperty("name", out var albumName) && albumName.ValueKind == JsonValueKind.String)
                album = albumName.GetString();

            if (albumObj.TryGetProperty("artists", out var albumArtists)
                && albumArtists.ValueKind == JsonValueKind.Array)
            {
                var names = albumArtists.EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetProperty("name").GetString())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
                if (names.Count > 0) albumArtist = string.Join(", ", names);
            }

            if (albumObj.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                // Spotify does not promise an order: take the largest copy
                // instead of whatever happens to be listed first (the old
                // code embedded the smallest one).
                string? bestUrl = null;
                var bestPixels = long.MinValue;
                foreach (var image in images.EnumerateArray())
                {
                    if (!image.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                        continue;
                    var width = image.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
                        ? w.GetInt64() : 0;
                    var height = image.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
                        ? h.GetInt64() : 0;
                    // Some payloads carry only one dimension; a square of
                    // the known side is the honest comparison then.
                    var pixels = width * (height > 0 ? height : width);
                    if (bestUrl is null || pixels > bestPixels)
                    {
                        bestUrl = url.GetString();
                        bestPixels = pixels;
                    }
                }
                coverUrl = bestUrl;
            }
        }

        return new Track
        {
            Title = track.TryGetProperty("name", out var name) ? name.GetString() : null,
            Artist = artists,
            Album = album,
            AlbumArtist = albumArtist,
            CoverUrl = coverUrl,
            Duration = durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
            SongID = new SongID(Platform.Spotify, id),
            PlatformSongID = new SongID(Platform.Spotify, id),
            SourcePlatform = Platform.Spotify,
            Isrc = isrc,
        };
    }
}
