using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// ISourceProvider for Deezer playlists. Deezer's public API needs no
/// account: the playlist payload carries the whole track list for common
/// sizes (with the ISRC of every track), and the tracks.next field is
/// followed when a playlist is longer than one page.
/// </summary>
public class DeezerSourceProvider : ISourceProvider
{
    /// <summary>Sanity cap: a playlist longer than this is not what a person shares.</summary>
    public const int MaxTracks = 5000;

    private readonly HttpClient _http;
    private readonly ILogger<DeezerSourceProvider> _logger;

    public string Name => "Deezer";
    public Platform Platform => Platform.Deezer;

    public DeezerSourceProvider(HttpClient http, ILogger<DeezerSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public bool CanHandle(string sourceIdentifier)
        => !string.IsNullOrWhiteSpace(sourceIdentifier)
           && sourceIdentifier.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)
           && sourceIdentifier.Contains("/playlist/", StringComparison.OrdinalIgnoreCase);

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var id = ExtractPlaylistId(sourceIdentifier)
            ?? throw new InvalidOperationException($"Not a Deezer playlist URL: {sourceIdentifier}");

        var url = $"https://api.deezer.com/playlist/{id}";
        _logger.LogInformation("Fetching Deezer playlist: {Url}", url);

        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        var playlist = ParsePlaylist(doc.RootElement, $"https://www.deezer.com/playlist/{id}");

        // Longer playlists page through tracks.next until it runs out.
        var next = ReadTracksNext(doc.RootElement);
        var pages = 0;
        while (next is not null && next.Length > 0
            && playlist.Tracks!.Count < MaxTracks && pages < 50)
        {
            pages++;
            using var pageResp = await _http.GetAsync(next);
            if (!pageResp.IsSuccessStatusCode) break;
            using var pageDoc = JsonDocument.Parse(await pageResp.Content.ReadAsStreamAsync());
            if (!pageDoc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var track in data.EnumerateArray())
            {
                var parsed = ParseTrack(track);
                if (parsed is not null) playlist.Tracks!.Add(parsed);
            }
            next = ReadTracksNext(pageDoc.RootElement);
        }

        playlist.Tracks = playlist.Tracks!.Take(MaxTracks).ToList();
        playlist.TrackCount = playlist.Tracks.Count;
        if (playlist.TrackCount != ReadInt(doc.RootElement, "nb_tracks") && ReadInt(doc.RootElement, "nb_tracks") > playlist.TrackCount)
            playlist.Warning = $"the playlist declares {ReadInt(doc.RootElement, "nb_tracks")} tracks but {playlist.TrackCount} arrived";

        _logger.LogInformation("Fetched Deezer playlist '{Title}' with {Count} tracks",
            playlist.Name, playlist.TrackCount);
        return playlist;
    }

    private static string? ReadTracksNext(JsonElement root)
    {
        if (root.TryGetProperty("tracks", out var tracks)
            && tracks.TryGetProperty("next", out var next)
            && next.ValueKind == JsonValueKind.String)
            return next.GetString();
        return null;
    }

    /// <summary>The playlist id out of a deezer.com/playlist/{id} URL. Internal for tests.</summary>
    internal static string? ExtractPlaylistId(string input)
    {
        var marker = "/playlist/";
        var index = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var rest = input[(index + marker.Length)..];
        var end = rest.IndexOfAny(['?', '/', '#', '&']);
        var id = end >= 0 ? rest[..end] : rest;
        return id.Length > 0 ? id : null;
    }

    /// <summary>Maps the public API payload into a Playlist. Internal for tests.</summary>
    internal static Playlist ParsePlaylist(JsonElement root, string sourceUrl)
    {
        var tracks = new List<Track>();
        if (root.TryGetProperty("tracks", out var tracksElement)
            && tracksElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var track in data.EnumerateArray())
            {
                var parsed = ParseTrack(track);
                if (parsed is not null) tracks.Add(parsed);
            }
        }

        return new Playlist
        {
            Id = ReadId(root, "id"),
            Name = ReadString(root, "title") ?? "Unknown playlist",
            Owner = root.TryGetProperty("creator", out var creator) ? ReadString(creator, "name") : null,
            Description = ReadString(root, "description"),
            CoverImageUrl = ReadString(root, "picture_big") ?? ReadString(root, "picture_xl"),
            SourceUrl = sourceUrl,
            Tracks = tracks,
            TrackCount = tracks.Count,
        };
    }

    private static Track? ParseTrack(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object) return null;
        var id = ReadId(track, "id");
        var title = ReadString(track, "title");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return null;

        var seconds = track.TryGetProperty("duration", out var duration)
            && duration.ValueKind == JsonValueKind.Number
            ? duration.GetInt32()
            : 0;

        return new Track
        {
            Title = title,
            Artist = track.TryGetProperty("artist", out var artist) ? ReadString(artist, "name") : null,
            Album = track.TryGetProperty("album", out var album) ? ReadString(album, "title") : null,
            Isrc = ReadString(track, "isrc"),
            Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
            SongID = new SongID(Platform.Deezer, id),
            PlatformSongID = new SongID(Platform.Deezer, id),
            SourcePlatform = Platform.Deezer,
            SyncStatus = SyncStatus.Pending,
            MediaType = MediaType.MP3,
            CurrentTrackLocation = new FileLocation($"https://www.deezer.com/track/{id}"),
        };
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Deezer's ids are JSON numbers, not strings; both shapes are read.</summary>
    private static string? ReadId(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var id) => id.ToString(),
            _ => null,
        };
    }

    private static int ReadInt(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
