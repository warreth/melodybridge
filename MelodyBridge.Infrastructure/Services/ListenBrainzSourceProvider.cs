using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// ISourceProvider for ListenBrainz playlists. Public playlists need no
/// account: the API answers in JSPF, and every track carries its title,
/// artist, album, duration and MusicBrainz recording id. ListenBrainz is
/// how the weekly recommendation and "created for you" playlists arrive,
/// and its playlist search is a good way to find community lists.
/// </summary>
public class ListenBrainzSourceProvider : ISourceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<ListenBrainzSourceProvider> _logger;

    public string Name => "ListenBrainz";
    public Platform Platform => Platform.Unknown;

    public ListenBrainzSourceProvider(HttpClient http, ILogger<ListenBrainzSourceProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public bool CanHandle(string sourceIdentifier)
        => !string.IsNullOrWhiteSpace(sourceIdentifier)
           && (sourceIdentifier.Contains("listenbrainz.org/playlist/", StringComparison.OrdinalIgnoreCase)
               || IsPlaylistMbid(sourceIdentifier));

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var mbid = ExtractPlaylistMbid(sourceIdentifier)
            ?? throw new InvalidOperationException($"Not a ListenBrainz playlist: {sourceIdentifier}");

        var url = $"https://api.listenbrainz.org/1/playlist/{mbid}";
        _logger.LogInformation("Fetching ListenBrainz playlist: {Url}", url);

        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        var playlist = ParsePlaylist(doc.RootElement, $"https://listenbrainz.org/playlist/{mbid}");

        _logger.LogInformation("Fetched ListenBrainz playlist '{Title}' with {Count} tracks",
            playlist.Name, playlist.TrackCount);
        return playlist;
    }

    /// <summary>A raw 36-char playlist MBID is accepted as an identifier too.</summary>
    internal static bool IsPlaylistMbid(string input)
        => Guid.TryParse(input.Trim(), out _);

    /// <summary>The playlist MBID out of a URL or a raw id. Internal for tests.</summary>
    internal static string? ExtractPlaylistMbid(string input)
    {
        var text = (input ?? "").Trim();
        var marker = "/playlist/";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var rest = text[(index + marker.Length)..];
            var end = rest.IndexOfAny(['?', '/', '#', '&']);
            text = end >= 0 ? rest[..end] : rest;
        }
        return Guid.TryParse(text, out _) ? text : null;
    }

    /// <summary>Maps the JSPF payload into a Playlist. Internal for tests.</summary>
    internal static Playlist ParsePlaylist(JsonElement root, string sourceUrl)
    {
        var tracks = new List<Track>();
        if (root.TryGetProperty("playlist", out var playlist) == false)
            throw new InvalidOperationException("ListenBrainz answered without a playlist");

        if (playlist.TryGetProperty("track", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var parsed = ParseTrack(item);
                if (parsed is not null) tracks.Add(parsed);
            }
        }

        var annotation = ReadString(playlist, "annotation");
        return new Playlist
        {
            Id = ReadString(playlist, "identifier") ?? sourceUrl,
            Name = ReadString(playlist, "title") ?? "Unknown playlist",
            Owner = ReadString(playlist, "creator"),
            Description = string.IsNullOrWhiteSpace(annotation) ? null : annotation,
            CoverImageUrl = FindCover(playlist),
            SourceUrl = sourceUrl,
            Tracks = tracks,
            TrackCount = tracks.Count,
        };
    }

    private static Track? ParseTrack(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var title = ReadString(item, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        // JSPF durations are milliseconds.
        var milliseconds = item.TryGetProperty("duration", out var duration)
            && duration.ValueKind == JsonValueKind.Number
            ? duration.GetInt64()
            : 0;

        var recordingId = FindRecordingId(item);
        var artist = ReadString(item, "creator");
        var album = ReadString(item, "album");
        var coverUrl = FindCover(item);

        // The recording MBID has no platform of its own; the id keeps the
        // mb: prefix so it can never collide with a platform id.
        var songId = new SongID(Platform.Unknown, "mb:" + (recordingId ?? FallbackKey(artist, title)));
        return new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            CoverUrl = coverUrl,
            Duration = milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : null,
            SongID = songId,
            PlatformSongID = songId,
            SourcePlatform = Platform.Unknown,
            SyncStatus = SyncStatus.Pending,
            MediaType = MediaType.MP3,
        };
    }

    /// <summary>The MusicBrainz recording MBID out of the JSPF identifiers.</summary>
    private static string? FindRecordingId(JsonElement item)
    {
        if (!item.TryGetProperty("identifier", out var identifiers)
            || identifiers.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var identifier in identifiers.EnumerateArray())
        {
            if (identifier.ValueKind != JsonValueKind.String) continue;
            var value = identifier.GetString()!;
            const string marker = "musicbrainz.org/recording/";
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var mbid = value[(index + marker.Length)..].TrimEnd('/');
            if (Guid.TryParse(mbid, out _)) return mbid;
        }
        return null;
    }

    /// <summary>
    /// Cover art when the JSPF extension carries a Cover Art Archive id.
    /// Works for a playlist node and for one track node: both keep the id
    /// under their module's additional_metadata.
    /// </summary>
    private static string? FindCover(JsonElement playlist)
    {
        if (!playlist.TryGetProperty("extension", out var extension)
            || extension.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var module in extension.EnumerateObject())
        {
            if (!module.Value.TryGetProperty("additional_metadata", out var metadata)) continue;
            if (metadata.TryGetProperty("caa_release_mbid", out var release)
                && release.ValueKind == JsonValueKind.String
                && Guid.TryParse(release.GetString(), out _))
                return $"https://coverartarchive.org/release/{release.GetString()}/front-500";
        }
        return null;
    }

    private static string FallbackKey(string? artist, string? title)
        => $"{artist}|{title}".ToLowerInvariant();

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
