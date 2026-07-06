using System.Diagnostics;
using System.Text.Json;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// ISourceProvider implementation for YouTube.
/// Uses yt-dlp to fetch playlist metadata and track listings.
/// </summary>
public class YouTubeSourceProvider : ISourceProvider
{
    private readonly ILogger<YouTubeSourceProvider> _logger;

    public string Name => "YouTube";
    public Platform Platform => Platform.YouTubeMusic;

    public YouTubeSourceProvider(ILogger<YouTubeSourceProvider> logger)
    {
        _logger = logger;
    }

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        // sourceIdentifier is a YouTube playlist URL
        var url = sourceIdentifier;
        if (!url.StartsWith("http"))
            url = $"https://www.youtube.com/playlist?list={sourceIdentifier}";

        _logger.LogInformation("Fetching YouTube playlist: {url}", url);

        var (exitCode, stdout, stderr) = await RunYtDlpAsync(
            $"--flat-playlist --dump-single-json --no-warnings --skip-download \"{url}\"");

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogError("yt-dlp failed (exit {code}): {err}", exitCode, stderr);
            throw new InvalidOperationException($"Failed to fetch playlist: {stderr}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Unknown" : "Unknown";
        var entries = root.TryGetProperty("entries", out var e) ? e : default;

        var tracks = new List<Track>();

        if (entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var trackUrl = entry.TryGetProperty("url", out var u)
                    ? $"https://www.youtube.com/watch?v={u.GetString()}"
                    : null;

                var entryTitle = entry.TryGetProperty("title", out var et)
                    ? et.GetString() ?? "Unknown"
                    : "Unknown";

                var uploader = entry.TryGetProperty("uploader", out var up)
                    ? up.GetString()
                    : entry.TryGetProperty("channel", out var ch)
                        ? ch.GetString()
                        : null;

                var trackId = entry.TryGetProperty("id", out var vId)
                    ? vId.GetString()
                    : null;

                if (trackUrl == null || trackId == null) continue;

                tracks.Add(new Track
                {
                    Title = entryTitle,
                    Artist = uploader ?? "Unknown",
                    SongID = new SongID(Platform.YouTubeMusic, trackId),
                    PlatformSongID = new SongID(Platform.YouTubeMusic, trackId),
                    SourcePlatform = Platform.YouTubeMusic,
                    SyncStatus = SyncStatus.Pending,
                    MediaType = MediaType.MP3,
                    CurrentTrackLocation = trackUrl != null ? new FileLocation(trackUrl) : null,
                });
            }
        }

        _logger.LogInformation("Fetched playlist '{name}' with {count} tracks", title, tracks.Count);
        return new Playlist { Name = title, Tracks = tracks };
    }

    public async Task<string?> ResolveTrackUrlAsync(string query)
    {
        // If already a URL, return as-is
        if (query.StartsWith("http"))
            return query;

        // Search YouTube for the query and return first result URL
        var (exitCode, stdout, stderr) = await RunYtDlpAsync(
            $"--flat-playlist --dump-single-json --no-warnings --skip-download \"ytsearch1:{query}\"");

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var entries = root.TryGetProperty("entries", out var e) ? e : root;
            if (entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            {
                var first = entries[0];
                var id = first.TryGetProperty("id", out var vid) ? vid.GetString() : null;
                if (id != null)
                    return $"https://www.youtube.com/watch?v={id}";
            }
        }
        catch { }

        return null;
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunYtDlpAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        await proc.WaitForExitAsync();

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
