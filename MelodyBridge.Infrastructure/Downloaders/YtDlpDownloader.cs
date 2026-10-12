using System.Text.Json;
using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// yt-dlp downloader plugin.
///
/// Searches YouTube Music first (falls back to plain YouTube) with
/// "artist title" queries, ranks the top results by title/artist
/// similarity plus duration proximity, downloads the best audio stream
/// as an audio-only file, and writes artist/title metadata tags.
/// </summary>
public partial class YtDlpDownloader : IDownloader
{
    private readonly ILogger<YtDlpDownloader> _logger;

    // yt-dlp transcodes to mp3/opus/aac via -x --audio-format: lossy 64-320 kbps, no lossless.
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Mp3, AudioFormat.Opus, AudioFormat.Aac], 64, 320, SupportsLossless: false, SupportsLossy: true);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "ytdlp";
    public string Name => "yt-dlp (YouTube)";
    public string Description => "YouTube: best audio as MP3";

    public YtDlpDownloader(ILogger<YtDlpDownloader> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // A missing binary fails fast here so the waterfall can skip us.
        return Task.FromResult(YtDlpProcess.BinaryPath is not null);
    }

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        if (YtDlpProcess.BinaryPath is null) return null;

        // YouTube search is noisy enough already: dropping mix suffixes
        // and featuring tags keeps the real track on page one.
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        // Top 5, ranked by title/artist similarity plus duration
        // proximity: never blindly the first row, which is routinely a
        // lyric video or a remix. (The old ytmsearch pseudo-extractor is
        // gone from current yt-dlp versions; ytsearch covers the catalog.)
        // YouTube does not index ISRCs, so the ISRC is not used here.
        var hits = await SearchExtractorAsync("ytsearch5", query.Artist, query.Title, searchText, ct);
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, hits);
    }

    /// <summary>Runs one search extractor and parses every flat entry as a candidate.</summary>
    private async Task<List<DownloaderSearchHit>> SearchExtractorAsync(
        string extractor, string artist, string title, string query, CancellationToken ct)
    {
        try
        {
            var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
            {
                $"{extractor}:{query}",
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
            }, TimeSpan.FromSeconds(30), ct);

            if (exit != 0) return new List<DownloaderSearchHit>();
            return ParseFlatPlaylistHits(stdout, artist, title,
                id => $"https://www.youtube.com/watch?v={id}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "yt-dlp search via {Extractor} failed for '{Query}'", extractor, query);
            return new List<DownloaderSearchHit>();
        }
    }

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (YtDlpProcess.BinaryPath is null)
            return new DownloaderDownloadResult(false, null, "yt-dlp binary not found");

        try
        {
            Directory.CreateDirectory(outputDirectory);

            // Deterministic filename so re-downloads replace instead of duplicate.
            var template = Path.Combine(outputDirectory, "%(id)s.%(ext)s");
            quality ??= DownloadQuality.Any;
            var args = YtDlpProcess.BuildDownloadArgs(template, sourceUrl, quality);

            var (exit, stdout, stderr) = await YtDlpProcess.RunDownloadAsync(args, TimeSpan.FromMinutes(5), ct);

            if (exit != 0)
                return new DownloaderDownloadResult(false, null, $"yt-dlp exited {exit}: {Truncate(stderr, 400)}");

            // Locate the produced file (the JSON's filepath, or the video
            // id + audio extension; never "the newest file", which races
            // concurrent downloads into the same folder).
            var file = FindProducedFile(stdout, outputDirectory);
            if (file is null)
                return new DownloaderDownloadResult(false, null, "yt-dlp reported success but no output file found");

            // YouTube hands out its best audio as opus inside a .webm
            // container, which no tag library can write. Remux losslessly
            // (-c copy) to .opus so MELODY_ID tagging and reconciliation
            // keep working. Without ffmpeg the webm stays (still playable).
            file = RemuxWebmToOpus(file) ?? file;

            TagDownloadedFile(file, expectedTitle: null);
            return new DownloaderDownloadResult(true, file, null);
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Parses every entry of a flat playlist JSON into a candidate hit.
    /// Shared by all yt-dlp-backed plugins; the caller ranks the list.
    /// </summary>
    internal static List<DownloaderSearchHit> ParseFlatPlaylistHits(
        string stdout, string artist, string fallbackTitle, Func<string, string> urlFromId)
    {
        var hits = new List<DownloaderSearchHit>();
        if (string.IsNullOrWhiteSpace(stdout)) return hits;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var entries = root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().ToList()
                : new List<JsonElement> { root };
            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var hit = ParseEntry(entry, artist, fallbackTitle, urlFromId);
                if (hit is not null) hits.Add(hit);
            }
        }
        catch (JsonException)
        {
            // Unparseable yt-dlp output is a plugin miss, not a crash.
        }
        return hits;
    }

    private static DownloaderSearchHit? ParseEntry(
        JsonElement entry, string artist, string fallbackTitle, Func<string, string> urlFromId)
    {
        var id = entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
        var sourceUrl = ExtractSourceUrl(entry, id, urlFromId);
        if (string.IsNullOrEmpty(sourceUrl)) return null;

        var hitTitle = (entry.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null) ?? fallbackTitle;

        // The real artist: flat entries carry the channel/uploader; the old
        // code compared the requested artist against the hit TITLE, so
        // "Regard" never matched "Regard - Ride It (Official Video)".
        var hitArtist = entry.TryGetProperty("uploader", out var up) && up.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(up.GetString()) ? up.GetString()
            : entry.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ch.GetString()) ? ch.GetString()
                : SplitArtistFromTitle(hitTitle) ?? artist;
        // Channel decorations are not part of the name.
        hitArtist = StripChannelDecorations(hitArtist);

        return new DownloaderSearchHit(
            Title: hitTitle,
            Artist: hitArtist,
            SourceUrl: sourceUrl,
            Duration: entry.TryGetProperty("duration", out var d) && d.TryGetDouble(out var secs)
                ? TimeSpan.FromSeconds(secs)
                : null,
            MatchConfidence: FuzzyMatcher.Confidence(
                artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
    }

    /// <summary>
    /// The entry's URL: a full webpage URL when the extractor provides
    /// one, otherwise built from the bare id. A bare non-URL "url" field
    /// (some extractors put just the id there) is never passed through.
    /// </summary>
    private static string? ExtractSourceUrl(JsonElement entry, string? id, Func<string, string> urlFromId)
    {
        foreach (var property in new[] { "webpage_url", "url" })
        {
            if (!entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            var text = value.GetString();
            if (!string.IsNullOrEmpty(text) && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return text;
        }
        return string.IsNullOrEmpty(id) ? null : urlFromId(id);
    }

    /// <summary>"Artist - Title" uploader strings: splits off the artist.</summary>
    private static string? SplitArtistFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var parts = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 ? parts[0] : null;
    }

    /// <summary>
    /// Drops the suffixes YouTube glues onto channel names ("RegardVEVO",
    /// "Regard - Topic", "Regard Official") so the artist inside still
    /// matches what was requested.
    /// </summary>
    private static string StripChannelDecorations(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist ?? "";
        var trimmed = artist.Trim();
        foreach (var suffix in new[] { " - Topic", "VEVO", " Official", "Official" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed[..^suffix.Length].Trim();
                if (stripped.Length > 0) trimmed = stripped;
            }
        }
        return trimmed;
    }

    /// <summary>
    /// Metadata tagging shared by all yt-dlp-backed plugins. MELODY_ID is
    /// no longer written here: DownloadManager writes it once for every
    /// plugin after the file passes the quality gate.
    /// </summary>
    internal static void TagDownloadedFile(string file, string? expectedTitle)
    {
        try
        {
            var embedded = Path.GetFileNameWithoutExtension(file);
            var sep = embedded.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0 && sep < embedded.Length - 3)
                Tagging.TaglibHelper.WriteTags(file,
                    title: embedded[(sep + 3)..].Trim(),
                    artist: embedded[..sep].Trim());
            else if (!string.IsNullOrWhiteSpace(expectedTitle))
                Tagging.TaglibHelper.WriteTags(file, title: expectedTitle);
        }
        catch { /* tagging must never fail a download */ }
    }

    /// <summary>
    /// Locates the produced file: the JSON's own filepath when present,
    /// otherwise the entry matching the video id and an audio extension.
    /// Intermediate fragment files (".f251.", ".part") are never final
    /// output. No "newest file" fallback: with parallel downloads into one
    /// folder that returns another track's file.
    /// </summary>
    internal static string? FindProducedFile(string jsonOutput, string outputDirectory)
    {
        string? videoId = null;
        string? reported = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                videoId = id.GetString();
            if (root.TryGetProperty("filepath", out var fp) && fp.ValueKind == JsonValueKind.String)
                reported = fp.GetString();
        }
        catch
        {
            // JSON parse failed (e.g. warnings on stdout): fall through.
        }

        if (!string.IsNullOrEmpty(reported) && File.Exists(reported)
            && IsAudioExtension(Path.GetExtension(reported)))
            return reported;

        if (videoId is not null)
        {
            var byId = Directory.EnumerateFiles(outputDirectory)
                .FirstOrDefault(p => IsFinalAudioFile(Path.GetFileName(p), videoId));
            if (byId is not null) return byId;
        }

        // Last resort when the JSON was unreadable: the single fresh audio
        // file in the folder. Only when there is exactly one, so parallel
        // downloads cannot hand back the wrong track.
        var fresh = Directory.EnumerateFiles(outputDirectory)
            .Where(p => IsAudioExtension(Path.GetExtension(p)))
            .Where(p => File.GetLastWriteTimeUtc(p) > DateTime.UtcNow.AddMinutes(-15))
            .Take(2)
            .ToList();
        return fresh.Count == 1 ? fresh[0] : null;
    }

    /// <summary>True for "<id>.opus"/"<id> - title.mp3"-style final files, false for ".f251.webm" fragments.</summary>
    private static bool IsFinalAudioFile(string fileName, string videoId)
    {
        if (!IsAudioExtension(Path.GetExtension(fileName))) return false;
        if (!fileName.Contains(videoId, StringComparison.Ordinal)) return false;
        return !FragmentPattern().IsMatch(fileName);
    }

    private static bool IsAudioExtension(string? ext)
        => ext is not null && ext.ToLowerInvariant() is ".mp3" or ".m4a" or ".opus" or ".ogg" or ".flac" or ".wav" or ".webm";

    /// <summary>yt-dlp's per-format fragments look like "name.f251.webm".</summary>
    [GeneratedRegex(@"\.f\d+\.", RegexOptions.CultureInvariant)]
    private static partial Regex FragmentPattern();

    /// <summary>
    /// Losslessly re-containers a .webm download as .opus so it becomes
    /// taggable (YouTube's best audio is opus inside .webm, which no tag
    /// library can write). Returns the new path, or null when impossible.
    /// </summary>
    internal static string? RemuxWebmToOpus(string file)
    {
        if (!file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return null;

        var ffmpeg = Audio.SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) return null;

        var target = Path.ChangeExtension(file, ".opus");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // -map 0:a:0: take the audio stream only, whatever else the
            // container carries, so the opus muxer always accepts it.
            foreach (var arg in new[] { "-y", "-v", "error", "-i", file, "-map", "0:a:0", "-c", "copy", target })
                psi.ArgumentList.Add(arg);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)) || process.ExitCode != 0 || !File.Exists(target))
            {
                try { File.Delete(target); } catch { /* best effort */ }
                return null;
            }

            try { File.Delete(file); } catch { /* keep both, harmless */ }
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
