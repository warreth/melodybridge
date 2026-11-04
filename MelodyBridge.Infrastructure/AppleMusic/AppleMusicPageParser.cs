using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.AppleMusic;

/// <summary>
/// Reads a public Apple Music playlist page. The page ships its view model in
/// a serialized-server-data script, which is the same data the web player
/// draws on first paint. It has no ISRC, so the API path is preferred; this
/// parser keeps public playlists working when that token or API is down.
/// </summary>
public static partial class AppleMusicPageParser
{
    [GeneratedRegex(
        """<script[^>]*id="serialized-server-data"[^>]*>(?<json>.*?)</script>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SerializedDataRegex();

    /// <summary>
    /// Parses the page into a playlist. Throws when the page carries no
    /// playlist data, which is what Apple serves for private, deleted or
    /// region-blocked playlists.
    /// </summary>
    public static Playlist Parse(string html, string playlistId, string canonicalUrl)
    {
        var match = SerializedDataRegex().Match(html ?? string.Empty);
        if (!match.Success)
            throw new InvalidOperationException(
                "Apple Music's public playlist page did not include its data. "
                + "The playlist must be public or shared with a link.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["json"].Value));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Apple Music's public playlist page carried unreadable data. "
                + "The playlist must be public or shared with a link.", ex);
        }

        using (document)
        {
            var sections = AppleMusicMapper.Prop(
                document.RootElement, "data", "0", "data", "sections");
            if (sections is not { ValueKind: JsonValueKind.Array })
                throw new InvalidOperationException(
                    "Apple Music's public playlist page did not include a playlist. "
                    + "The playlist must be public or shared with a link.");

            JsonElement? header = null;
            JsonElement? trackList = null;
            foreach (var section in sections.Value.EnumerateArray())
            {
                var id = AppleMusicMapper.String(section, "id") ?? string.Empty;
                if (id.StartsWith("playlist-detail-header-section", StringComparison.Ordinal))
                    header = section;
                else if (id.StartsWith("track-list", StringComparison.Ordinal)
                         && !id.Contains("footer", StringComparison.OrdinalIgnoreCase))
                    trackList = section;
            }

            if (header is null && trackList is null)
                throw new InvalidOperationException(
                    "Apple Music's public playlist page did not include a playlist. "
                    + "The playlist must be public or shared with a link.");

            var tracks = new List<Track>();
            if (trackList is { } list
                && AppleMusicMapper.Prop(list, "items") is { ValueKind: JsonValueKind.Array } items)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (MapTrack(item) is { } track) tracks.Add(track);
                }
            }

            var declared = AppleMusicMapper.Int(header, "items", "0", "trackCount");
            if (declared is > 0 && tracks.Count == 0)
                throw new InvalidOperationException(
                    "Apple Music's public playlist page listed no tracks. "
                    + "The playlist must be public or shared with a link.");

            var playlist = new Playlist
            {
                Id = playlistId,
                Name = AppleMusicMapper.String(header, "items", "0", "title")?.Trim() ?? "Apple Music playlist",
                Owner = AppleMusicMapper.String(header, "items", "0", "subtitleLinks", "0", "title")?.Trim(),
                SourceUrl = canonicalUrl,
                CoverImageUrl = Artwork(AppleMusicMapper.Prop(header, "items", "0")),
                Tracks = tracks,
                TrackCount = tracks.Count,
                Duration = SumDurations(tracks),
            };

            if (declared is { } total && total > tracks.Count)
                playlist.Warning = $"partial fetch: read {tracks.Count} of {total} tracks";

            return playlist;
        }
    }

    private static Track? MapTrack(JsonElement item)
    {
        var title = AppleMusicMapper.String(item, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var kind = AppleMusicMapper.String(item, "contentDescriptor", "kind");
        if (!string.IsNullOrWhiteSpace(kind)
            && !kind.Equals("song", StringComparison.OrdinalIgnoreCase))
            return null;

        var url = AppleMusicMapper.String(item, "contentDescriptor", "url");
        var songId = AppleMusicMapper.String(
                        item, "contentDescriptor", "identifiers", "storeAdamID")
                    ?? SongIdFromUrl(url);

        return new Track
        {
            Title = title.Trim(),
            Artist = AppleMusicMapper.String(item, "artistName")?.Trim() ?? string.Empty,
            Album = AppleMusicMapper.String(item, "tertiaryLinks", "0", "title")?.Trim(),
            CoverUrl = Artwork(item),
            Duration = Milliseconds(AppleMusicMapper.Long(item, "duration")),
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

    private static string? Artwork(JsonElement? item)
    {
        var url = AppleMusicMapper.String(item, "artwork", "dictionary", "url")
                  ?? AppleMusicMapper.String(item, "tallArtwork", "dictionary", "url");
        return string.IsNullOrWhiteSpace(url)
            ? null
            : url.Replace("{w}", "600", StringComparison.Ordinal)
                .Replace("{h}", "600", StringComparison.Ordinal)
                .Replace("{f}", "jpg", StringComparison.Ordinal);
    }

    private static string? SongIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var marker = url.IndexOf("?i=", StringComparison.Ordinal);
        if (marker < 0) marker = url.IndexOf("&i=", StringComparison.Ordinal);
        if (marker < 0) return null;
        var rest = url[(marker + 3)..];
        var end = rest.IndexOfAny(['&', '#']);
        return end >= 0 ? rest[..end] : rest;
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
