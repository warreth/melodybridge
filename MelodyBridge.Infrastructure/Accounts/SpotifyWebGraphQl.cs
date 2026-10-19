using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Accounts;

/// <summary>
/// The three Spotify internal GraphQL queries the unofficial connection
/// needs, plus parsers for their answers.
///
/// Spotify serves these to its own web player (api-partner.spotify.com,
/// persisted query hashes). The hashes are pinned constants: when Spotify
/// ships a new bundle the old hash stops answering, which is exactly why
/// this whole path is documented as unstable. The request and response
/// shapes follow the Jellyfin Spotify import plugin (Viperinius, GPL-3.0).
///
/// The parsers are deliberately tolerant: field wrappers (itemV2/track),
/// uri/_uri and duration/trackDuration all appear in the wild.
/// </summary>
internal static class SpotifyWebGraphQl
{
    internal const string QueryUrl = "https://api-partner.spotify.com/pathfinder/v2/query";

    internal const string LibraryPlaylistsHash =
        "9f4da031f81274d572cfedaf6fc57a737c84b43d572952200b2c36aaa8fec1c6";

    internal const string LikedTracksHash =
        "087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240";

    internal const string PlaylistHash =
        "19ff1327c29e99c208c86d7a9d8f1929cfdf3d3202a0ff4253c821f1901aa94d";

    internal static string LibraryPlaylistsBody(int limit, int offset)
        => Body("libraryV3", LibraryPlaylistsHash, new
        {
            limit,
            offset,
            filters = new[] { "Playlists" },
            order = (string?)null,
            textFilter = "",
            features = new[] { "LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "EVENTS" },
            flatten = true,
            expandedFolders = Array.Empty<string>(),
            folderUri = (string?)null,
            includeFoldersWhenFlattening = false,
        });

    internal static string LikedTracksBody(int limit, int offset)
        => Body("fetchLibraryTracks", LikedTracksHash, new { limit, offset });

    internal static string PlaylistBody(int limit, int offset, string playlistUri)
        => Body("fetchPlaylist", PlaylistHash, new { limit, offset, uri = playlistUri });

    private static string Body(string operation, string hash, object variables)
        => JsonSerializer.Serialize(new
        {
            variables,
            operationName = operation,
            extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } },
        });

    /// <summary>The "data" node of a 200 answer, or false when Spotify sent errors.</summary>
    internal static bool TryGetData(JsonElement root, out JsonElement data)
    {
        data = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
            return false;
        if (!root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
            return false;
        return true;
    }

    /// <summary>Walks a dotted path of object properties.</summary>
    internal static bool TryGetPath(JsonElement root, out JsonElement found, params string[] path)
    {
        found = root;
        foreach (var step in path)
        {
            if (found.ValueKind != JsonValueKind.Object
                || !found.TryGetProperty(step, out var next))
            {
                found = default;
                return false;
            }
            found = next;
        }
        return true;
    }

    internal static int TotalCount(JsonElement container)
        => container.ValueKind == JsonValueKind.Object
           && container.TryGetProperty("totalCount", out var total)
           && total.ValueKind == JsonValueKind.Number
            ? total.GetInt32()
            : 0;

    internal static IEnumerable<JsonElement> Items(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in items.EnumerateArray())
            yield return item;
    }

    /// <summary>
    /// The track object inside a library or playlist item. Liked songs wrap
    /// it in "track", playlist entries in "itemV2", and the bare "item"
    /// name appears in newer answers; the uri travels either on the wrapper
    /// (_uri) or inside the data.
    /// </summary>
    internal static bool TryGetTrackNode(JsonElement item, out string? wrapperUri, out JsonElement data)
    {
        wrapperUri = null;
        data = default;
        if (item.ValueKind != JsonValueKind.Object) return false;

        foreach (var wrapperName in new[] { "itemV2", "track", "item" })
        {
            if (!item.TryGetProperty(wrapperName, out var wrapper)
                || wrapper.ValueKind != JsonValueKind.Object)
                continue;

            if (wrapper.TryGetProperty("_uri", out var uriProp)
                && uriProp.ValueKind == JsonValueKind.String)
                wrapperUri = uriProp.GetString();

            if (!wrapper.TryGetProperty("data", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
                return false;

            data = payload;
            return true;
        }

        return false;
    }

    internal static string? StringField(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object
           && node.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>All artist names, in the order Spotify lists them.</summary>
    internal static string? ArtistNames(JsonElement artistsNode)
    {
        if (!TryGetPath(artistsNode, out var items, "items")
            || items.ValueKind != JsonValueKind.Array)
            return null;

        var names = items.EnumerateArray()
            .Select(entry => TryGetPath(entry, out var profile, "profile")
                ? StringField(profile, "name")
                : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>
    /// The album's cover art: the largest source wins, whatever order
    /// Spotify lists them in.
    /// </summary>
    internal static string? LargestCover(JsonElement albumNode)
    {
        if (!TryGetPath(albumNode, out var sources, "coverArt", "sources")
            || sources.ValueKind != JsonValueKind.Array)
            return null;

        return LargestUrl(sources);
    }

    private static string? LargestUrl(JsonElement sources)
    {
        string? bestUrl = null;
        long bestPixels = 0;
        foreach (var source in sources.EnumerateArray())
        {
            var url = StringField(source, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            var width = source.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number
                ? w.GetInt64() : 0;
            var height = source.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
                ? h.GetInt64() : 0;
            var pixels = width * (height > 0 ? height : width);

            if (bestUrl is null || pixels > bestPixels)
            {
                bestUrl = url;
                bestPixels = pixels;
            }
        }

        return bestUrl;
    }

    /// <summary>
    /// One user playlist out of the library listing. The liked-songs entry
    /// ("spotify:collection:tracks") is not a playlist and is skipped by
    /// the caller.
    /// </summary>
    internal static UserPlaylist? MapLibraryPlaylist(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("item", out var node) || node.ValueKind != JsonValueKind.Object)
            return null;

        var uri = StringField(node, "_uri") ?? StringField(node, "uri");
        var data = node.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object
            ? payload
            : default;
        if (uri is null || data.ValueKind != JsonValueKind.Object) return null;

        var id = uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)
            ? uri["spotify:playlist:".Length..]
            : null;
        if (string.IsNullOrEmpty(id)) return null;

        var name = StringField(data, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var count = data.TryGetProperty("count", out var countProp)
            && countProp.ValueKind == JsonValueKind.Number
                ? countProp.GetInt32()
                : 0;

        return new UserPlaylist(
            id,
            name,
            OwnerName(data),
            count,
            IsLikedSongs: false,
            PlaylistCover(data),
            IsOwned: true);
    }

    internal static string? OwnerName(JsonElement data)
    {
        if (!TryGetPath(data, out var owner, "ownerV2", "data"))
            return null;
        return StringField(owner, "name") ?? StringField(owner, "username");
    }

    /// <summary>Playlist cover: the API nests sources under images.items[].</summary>
    internal static string? PlaylistCover(JsonElement data)
    {
        if (!TryGetPath(data, out var images, "images")) return null;

        // images: { items: [ { sources: [...] } ] }
        if (TryGetPath(images, out var items, "items") && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (TryGetPath(entry, out var sources, "sources")
                    && sources.ValueKind == JsonValueKind.Array
                    && LargestUrl(sources) is { } url)
                    return url;
            }
        }

        // Some answers carry the sources directly.
        if (TryGetPath(images, out var direct, "sources") && direct.ValueKind == JsonValueKind.Array)
            return LargestUrl(direct);

        return null;
    }

    /// <summary>Maps one track payload into the domain model.</summary>
    internal static Track? MapTrack(JsonElement data, string? wrapperUri, bool isLiked)
    {
        // Podcasts live in playlists too; they are not downloadable music.
        var typeName = StringField(data, "__typename");
        if (typeName is "Episode" or "LocalTrack") return null;

        var uri = StringField(data, "uri") ?? wrapperUri;
        var id = uri is not null && uri.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? uri["spotify:track:".Length..]
            : null;
        if (string.IsNullOrEmpty(id)) return null;

        var title = StringField(data, "name");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var durationMs = Milliseconds(data, "duration") ?? Milliseconds(data, "trackDuration");

        var album = data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("albumOfTrack", out var albumNode)
            && albumNode.ValueKind == JsonValueKind.Object
                ? albumNode
                : default;

        var artists = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("artists", out var artistNode)
            ? ArtistNames(artistNode)
            : null;
        var albumArtists = album.ValueKind == JsonValueKind.Object
            && album.TryGetProperty("artists", out var albumArtistNode)
                ? ArtistNames(albumArtistNode)
                : null;

        return new Track
        {
            Title = title,
            Artist = artists,
            Album = album.ValueKind == JsonValueKind.Object ? StringField(album, "name") : null,
            AlbumArtist = albumArtists,
            CoverUrl = album.ValueKind == JsonValueKind.Object ? LargestCover(album) : null,
            Duration = durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
            SongID = new SongID(Platform.Spotify, id),
            PlatformSongID = new SongID(Platform.Spotify, id),
            SourcePlatform = Platform.Spotify,
            SyncStatus = SyncStatus.Pending,
            MediaType = MediaType.MP3,
            CurrentTrackLocation = new FileLocation($"https://open.spotify.com/track/{id}"),
            IsLiked = isLiked,
        };
    }

    private static long? Milliseconds(JsonElement node, string property)
    {
        if (!TryGetPath(node, out var duration, property)
            || duration.ValueKind != JsonValueKind.Object
            || !duration.TryGetProperty("totalMilliseconds", out var ms)
            || ms.ValueKind != JsonValueKind.Number)
            return null;
        return ms.GetInt64();
    }

    /// <summary>Playlist metadata (name, description, owner, cover) and its tracks.</summary>
    internal static Playlist? MapPlaylist(JsonElement playlistNode, IReadOnlyList<Track> tracks)
    {
        if (playlistNode.ValueKind != JsonValueKind.Object) return null;

        var name = StringField(playlistNode, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var uri = StringField(playlistNode, "uri") ?? "";
        var id = uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)
            ? uri["spotify:playlist:".Length..]
            : null;

        return new Playlist
        {
            Id = id,
            Name = name,
            Description = StringField(playlistNode, "description"),
            Owner = OwnerName(playlistNode),
            SourceUrl = id is null ? null : $"https://open.spotify.com/playlist/{id}",
            CoverImageUrl = PlaylistCover(playlistNode),
            TrackCount = tracks.Count,
            Duration = Sum(tracks),
            Tracks = tracks.ToList(),
        };
    }

    private static TimeSpan? Sum(IReadOnlyList<Track> tracks)
    {
        var total = TimeSpan.Zero;
        var any = false;
        foreach (var track in tracks)
        {
            if (track.Duration is not { } d) continue;
            total += d;
            any = true;
        }
        return any ? total : null;
    }
}
