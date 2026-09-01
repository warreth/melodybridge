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

        var (exitCode, stdout, stderr) = await YtDlpProcess.RunAsync(new[]
        {
            "--flat-playlist",
            "--dump-single-json",
            "--no-warnings",
            "--skip-download",
            url,
        }, TimeSpan.FromMinutes(3), CancellationToken.None);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogError("yt-dlp failed (exit {Code}): {Error}", exitCode, stderr);
            throw new InvalidOperationException($"Failed to fetch playlist: {stderr}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        var title = GetString(root, "title") ?? "Unknown playlist";
        var channel = GetString(root, "channel") ?? GetString(root, "uploader");
        var description = GetString(root, "description");

        var tracks = new List<Track>();
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var id = GetString(entry, "id");
                if (string.IsNullOrEmpty(id)) continue;

                TimeSpan? duration = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetDouble())
                    : null;

                tracks.Add(new Track
                {
                    Title = GetString(entry, "title") ?? "Unknown",
                    Artist = GetString(entry, "uploader")
                        ?? GetString(entry, "channel")
                        ?? "Unknown",
                    Duration = duration,
                    SongID = new SongID(Platform.YouTubeMusic, id),
                    PlatformSongID = new SongID(Platform.YouTubeMusic, id),
                    SourcePlatform = Platform.YouTubeMusic,
                    SyncStatus = SyncStatus.Pending,
                    MediaType = MediaType.MP3,
                    CurrentTrackLocation = new FileLocation($"https://www.youtube.com/watch?v={id}"),
                });
            }
        }

        _logger.LogInformation("Fetched YouTube playlist '{Title}' with {Count} tracks", title, tracks.Count);

        return new Playlist
        {
            Id = GetPlaylistId(url),
            Name = title,
            Owner = channel,
            Description = description,
            SourceUrl = url,
            Tracks = tracks,
            TrackCount = tracks.Count,
        };
    }

    public async Task<string?> ResolveTrackUrlAsync(string query)
    {
        if (query.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return query;

        // YouTube Music first (official audio uploads), plain YouTube as fallback.
        string? stdout = null;
        foreach (var extractor in new[] { "ytmsearch1", "ytsearch1" })
        {
            var (code, output, _) = await YtDlpProcess.RunAsync(new[]
            {
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
                "--skip-download",
                $"{extractor}:{query}",
            }, TimeSpan.FromSeconds(45), CancellationToken.None);

            if (code == 0 && !string.IsNullOrWhiteSpace(output))
            {
                stdout = output;
                break;
            }
        }

        if (stdout is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var entries = root.TryGetProperty("entries", out var e) ? e : root;
            if (entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            {
                var id = GetString(entries[0], "id");
                if (id is not null)
                    return $"https://www.youtube.com/watch?v={id}";
            }
        }
        catch { /* malformed search result: no hit */ }

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
