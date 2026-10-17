using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// ISourceProvider for SoundCloud sets (playlists). yt-dlp reads the set and
/// the shared flat-playlist mapping turns the entries into tracks.
///
/// Unlike YouTube, a SoundCloud set must be extracted entry by entry: the
/// flat mode reports no title or artist at all, so the set would land as a
/// list of "Unknown". Entries that only carry a Go+ preview stream (a 30s
/// clip of a DRM-protected track) are skipped, because downloading one
/// would silently put a 30-second file in the library; the playlist's
/// warning names how many were skipped.
/// </summary>
public class SoundCloudSourceProvider : ISourceProvider
{
    private readonly ILogger<SoundCloudSourceProvider> _logger;

    public string Name => "SoundCloud";
    public Platform Platform => Platform.Soundcloud;

    public SoundCloudSourceProvider(ILogger<SoundCloudSourceProvider> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(string sourceIdentifier)
        => !string.IsNullOrWhiteSpace(sourceIdentifier)
           && sourceIdentifier.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase)
           && sourceIdentifier.Contains("/sets/", StringComparison.OrdinalIgnoreCase);

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var url = sourceIdentifier.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? sourceIdentifier
            : "https://" + sourceIdentifier.TrimStart('/');

        _logger.LogInformation("Fetching SoundCloud set: {Url}", url);

        var (exitCode, stdout, stderr) = await FetchSetJsonAsync(url);
        if (!HasPlaylistJson(stdout))
        {
            _logger.LogWarning("yt-dlp set fetch failed (exit {Code}): {Error}; retrying once",
                exitCode, Truncate(stderr));
            await Task.Delay(TimeSpan.FromSeconds(2));
            (exitCode, stdout, stderr) = await FetchSetJsonAsync(url);
        }

        if (!HasPlaylistJson(stdout))
        {
            _logger.LogError("yt-dlp failed twice for the set (exit {Code}): {Error}", exitCode, stderr);
            throw new InvalidOperationException($"Failed to fetch the SoundCloud set after a retry: {Truncate(stderr)}");
        }

        // yt-dlp exits non-zero when individual entries are DRM protected
        // even with --ignore-errors, but still prints the full playlist
        // JSON. A usable JSON is the real success signal; the null entries
        // and the Go+ previews are reported instead of silently lost.
        var declared = CountEntries(stdout);
        var previews = 0;
        var playlist = YouTubeSourceProvider.ParseFlatPlaylist(
            stdout, url, Platform.Soundcloud,
            id => id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? id
                : $"https://soundcloud.com/{id}",
            entry =>
            {
                if (!IsPreviewOnly(entry)) return true;
                previews++;
                return false;
            });

        var skipped = declared - playlist.TrackCount;
        if (skipped > 0)
        {
            var reason = previews > 0
                ? $"{previews} Go+ preview(s)"
                : "DRM-protected or removed entries";
            playlist.Warning = $"{skipped} of {declared} SoundCloud track(s) could not be used ({reason})";
        }

        _logger.LogInformation("Fetched SoundCloud set '{Title}' with {Count} tracks ({Skipped} of {Declared} skipped)",
            playlist.Name, playlist.TrackCount, skipped, declared);
        return playlist;
    }

    /// <summary>True when stdout holds a complete yt-dlp playlist document.</summary>
    internal static bool HasPlaylistJson(string? stdout)
        => !string.IsNullOrWhiteSpace(stdout)
           && stdout.TrimStart().StartsWith('{')
           && stdout.Contains("\"entries\"", StringComparison.Ordinal);

    /// <summary>The number of entries yt-dlp declared, including unusable ones.</summary>
    internal static int CountEntries(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array
                ? entries.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// True when every stream yt-dlp offers for the entry is a preview
    /// clip: Go+ and DRM-protected catalogue tracks expose only the 30
    /// second preview to anonymous listeners.
    /// </summary>
    internal static bool IsPreviewOnly(JsonElement entry)
    {
        if (!entry.TryGetProperty("formats", out var formats)
            || formats.ValueKind != JsonValueKind.Array
            || formats.GetArrayLength() == 0)
            return false;

        foreach (var format in formats.EnumerateArray())
        {
            if (IsPreviewFormat(format)) continue;
            // Any real stream means the entry is downloadable in full.
            return false;
        }
        return true;
    }

    /// <summary>
    /// A Go+ preview format is marked by its id (<c>http_mp3_1_0_preview</c>)
    /// and, for the HLS variant, only by the id: the media URL itself is a
    /// plain cf-hls-media playlist. Both are checked.
    /// </summary>
    private static bool IsPreviewFormat(JsonElement format)
    {
        var formatId = format.TryGetProperty("format_id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? ""
            : "";
        var url = format.TryGetProperty("url", out var urlElement)
            && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString() ?? ""
            : "";
        return formatId.Contains("preview", StringComparison.OrdinalIgnoreCase)
            || url.Contains("preview", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full extraction on purpose (no --flat-playlist): the flat shape has
    /// no title, artist or duration for SoundCloud. --ignore-errors keeps
    /// one DRM-protected entry from failing the whole set.
    /// </summary>
    private static Task<(int exit, string stdout, string stderr)> FetchSetJsonAsync(string url)
        => YtDlpProcess.RunAsync(new[]
        {
            "--dump-single-json",
            "--no-warnings",
            "--ignore-errors",
            "--skip-download",
            url,
        }, TimeSpan.FromMinutes(8), CancellationToken.None);

    private static string Truncate(string? text)
        => string.IsNullOrWhiteSpace(text) ? "no error output"
            : text.Length <= 300 ? text : text[..300] + "...";
}
