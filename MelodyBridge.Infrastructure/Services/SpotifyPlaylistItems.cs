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

        long? durationMs = track.TryGetProperty("duration_ms", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt64()
            : null;

        var artists = track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array
            ? string.Join(", ", artistArray.EnumerateArray()
                .Where(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                .Select(a => a.GetProperty("name").GetString()))
            : null;

        return new Track
        {
            Title = track.TryGetProperty("name", out var name) ? name.GetString() : null,
            Artist = artists,
            Duration = durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
            SongID = new SongID(Platform.Spotify, id),
            PlatformSongID = new SongID(Platform.Spotify, id),
            SourcePlatform = Platform.Spotify,
        };
    }
}
