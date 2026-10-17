using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// ISourceProvider implementation for YouTube and YouTube Music.
/// Uses yt-dlp (via the shared YtDlpProcess helper) to fetch full playlist
/// track listings, including playlists longer than 100 tracks.
/// </summary>
public class YouTubeSourceProvider : ISourceProvider
{
    private readonly ILogger<YouTubeSourceProvider> _logger;

    public string Name => "YouTube";
    public Platform Platform => Platform.YouTubeMusic;

    public bool CanHandle(string sourceIdentifier)
        => !string.IsNullOrWhiteSpace(sourceIdentifier)
           && (sourceIdentifier.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
               || sourceIdentifier.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
               || sourceIdentifier.StartsWith("PL", StringComparison.Ordinal)
               || sourceIdentifier.StartsWith("OL", StringComparison.Ordinal)
               || sourceIdentifier.StartsWith("LL", StringComparison.Ordinal)
               || sourceIdentifier.StartsWith("RD", StringComparison.Ordinal));

    public YouTubeSourceProvider(ILogger<YouTubeSourceProvider> logger)
    {
        _logger = logger;
    }

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var url = sourceIdentifier.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? sourceIdentifier
            : $"https://www.youtube.com/playlist?list={sourceIdentifier}";

        _logger.LogInformation("Fetching YouTube playlist: {Url}", url);

        // yt-dlp talks to a third-party endpoint that hiccups; one retry
        // turns a transient failure into a slow fetch instead of a failed
        // sync.
        var (exitCode, stdout, stderr) = await FetchPlaylistJsonAsync(url);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogWarning("yt-dlp playlist fetch failed (exit {Code}): {Error}; retrying once",
                exitCode, Truncate(stderr));
            await Task.Delay(TimeSpan.FromSeconds(2));
            (exitCode, stdout, stderr) = await FetchPlaylistJsonAsync(url);
        }

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogError("yt-dlp failed twice (exit {Code}): {Error}", exitCode, stderr);
            throw new InvalidOperationException($"Failed to fetch playlist after a retry: {Truncate(stderr)}");
        }

        var playlist = ParsePlaylistJson(stdout, url);
        _logger.LogInformation("Fetched YouTube playlist '{Title}' with {Count} tracks",
            playlist.Name, playlist.Tracks?.Count ?? 0);
        return playlist;
    }

    private static Task<(int exit, string stdout, string stderr)> FetchPlaylistJsonAsync(string url)
        => YtDlpProcess.RunAsync(new[]
        {
            "--flat-playlist",
            "--dump-single-json",
            "--no-warnings",
            "--skip-download",
            url,
        }, TimeSpan.FromMinutes(3), CancellationToken.None);

    /// <summary>
    /// Maps yt-dlp's flat-playlist JSON into a Playlist: title, channel,
    /// description, cover and one Track per usable entry. Internal so the
    /// offline tests pin the mapping against a real response shape.
    /// </summary>
    internal static Playlist ParsePlaylistJson(string stdout, string url)
        => ParseFlatPlaylist(stdout, url, Platform.YouTubeMusic,
            id => $"https://www.youtube.com/watch?v={id}");

    /// <summary>
    /// The shared flat-playlist mapping: the same yt-dlp shape feeds the
    /// YouTube and SoundCloud providers, so only the platform and the
    /// fallback location differ.
    /// </summary>
    internal static Playlist ParseFlatPlaylist(
        string stdout, string url, Platform platform, Func<string, string> location,
        Func<JsonElement, bool>? entryFilter = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("yt-dlp returned unreadable JSON for the playlist", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var title = GetString(root, "title") ?? "Unknown playlist";
            var channel = GetString(root, "channel") ?? GetString(root, "uploader");
            var description = GetString(root, "description");
            var cover = PickThumbnail(root);

            var tracks = new List<Track>();
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    // Unavailable videos arrive as null stubs or without an id.
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(entry, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    if (entryFilter is not null && !entryFilter(entry)) continue;

                    TimeSpan? duration = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromSeconds(d.GetDouble())
                        : null;

                    // The entry's own URL wins when yt-dlp provides one
                    // (SoundCloud track pages); otherwise the caller's
                    // pattern builds it from the id.
                    var entryUrl = GetString(entry, "webpage_url") ?? GetString(entry, "url");
                    var trackUrl = !string.IsNullOrWhiteSpace(entryUrl)
                        && entryUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? entryUrl
                        : location(id);

                    tracks.Add(new Track
                    {
                        Title = GetString(entry, "title") ?? "Unknown",
                        Artist = GetString(entry, "uploader")
                            ?? GetString(entry, "channel")
                            ?? "Unknown",
                        Duration = duration,
                        SongID = new SongID(platform, id),
                        PlatformSongID = new SongID(platform, id),
                        SourcePlatform = platform,
                        SyncStatus = SyncStatus.Pending,
                        MediaType = MediaType.MP3,
                        CurrentTrackLocation = new FileLocation(trackUrl),
                    });
                }
            }

            return new Playlist
            {
                Id = GetPlaylistId(url),
                Name = title,
                Owner = channel,
                Description = description,
                CoverImageUrl = cover,
                SourceUrl = url,
                Tracks = tracks,
                TrackCount = tracks.Count,
            };
        }
    }

    private static string Truncate(string? text)
        => string.IsNullOrWhiteSpace(text) ? "no error output"
            : text.Length <= 300 ? text : text[..300] + "...";

    /// <summary>
    /// Picks the best playlist cover from a yt-dlp --dump-single-json doc:
    /// the tallest entry of the root "thumbnails" array, else the root
    /// "thumbnail" string, else the first entry's thumbnail. Null when the
    /// doc exposes no thumbnail at all.
    /// </summary>
    internal static string? PickThumbnail(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("thumbnails", out var thumbs)
            && thumbs.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            var bestHeight = -1;
            foreach (var t in thumbs.EnumerateArray())
            {
                var url = GetString(t, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                var height = t.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
                    ? h.GetInt32()
                    : 0;
                if (height > bestHeight)
                {
                    bestHeight = height;
                    best = url;
                }
            }
            if (best is not null) return best;
        }

        var single = GetString(root, "thumbnail");
        if (!string.IsNullOrWhiteSpace(single)) return single;

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var entryThumb = GetString(entry, "thumbnail");
                if (!string.IsNullOrWhiteSpace(entryThumb)) return entryThumb;
            }
        }

        return null;
    }

    /// <summary>Extracts the list= parameter as the stable playlist ID.</summary>
    internal static string GetPlaylistId(string url)
    {
        var marker = "list=";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return url;
        var rest = url[(idx + marker.Length)..];
        var amp = rest.IndexOf('&');
        return amp >= 0 ? rest[..amp] : rest;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
