using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// yt-dlp downloader plugin.
///
/// Searches YouTube Music first (falls back to plain YouTube) with
/// "artist title" queries, downloads the best audio as MP3, and writes
/// MELODY_ID + artist/title metadata tags into the file.
/// </summary>
public class YtDlpDownloader : IDownloader
{
    private readonly ILogger<YtDlpDownloader> _logger;

    // yt-dlp transcodes to mp3/opus/aac via -x --audio-format: lossy 64-320 kbps, no lossless.
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Mp3, AudioFormat.Opus, AudioFormat.Aac], 64, 320, SupportsLossless: false, SupportsLossy: true);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "ytdlp";
    public string Name => "yt-dlp (YouTube)";
    public string Description => "YouTube / YouTube Music: best audio as MP3";

    public YtDlpDownloader(ILogger<YtDlpDownloader> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // A missing binary fails fast here so the waterfall can skip us.
        return Task.FromResult(YtDlpProcess.BinaryPath is not null);
    }

    public async Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
    {
        if (YtDlpProcess.BinaryPath is null) return null;

        var query = $"{artist} {title}".Trim();
        if (query.Length == 0) return null;

        // Try YouTube Music first for better audio matches; fall back to YouTube.
        foreach (var extractor in new[] { "ytmsearch1", "ytsearch1" })
        {
            try
            {
                var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
                {
                    $"{extractor}:{query}",
                    "--flat-playlist",
                    "--dump-single-json",
                    "--no-warnings",
                }, TimeSpan.FromSeconds(45), ct);

                var hit = ParseFlatPlaylistHit(stdout, artist, title,
                    id => $"https://www.youtube.com/watch?v={id}");
                if (hit is not null) return hit;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "yt-dlp search via {Extractor} failed for '{Query}'", extractor, query);
            }
        }

        return null;
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

            // Auto = keep the source codec (no transcode, no quality loss, no
            // fake-bitrate files). Any explicit format transcodes with the
            // requested bitrate cap as the VBR target.
            var args = new List<string> { "-o", template, sourceUrl, "--print-json", "--no-simulate" };
            if (quality.NeedsTranscode)
            {
                args.InsertRange(0, new[]
                {
                    "-x",
                    "--audio-format", quality!.Format.ToString().ToLowerInvariant(),
                    "--audio-quality", quality.YtDlpAudioQuality,
                    "--embed-metadata",
                });
            }

            var (exit, stdout, stderr) = await YtDlpProcess.RunAsync(args.ToArray(), TimeSpan.FromMinutes(5), ct);

            if (exit != 0)
                return new DownloaderDownloadResult(false, null, $"yt-dlp exited {exit}: {Truncate(stderr, 400)}");

            // Locate the produced file (id from JSON confirms which one is ours).
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
    /// Parses yt-dlp --dump-single-json output (flat playlist) into a search hit.
    /// Shared by all yt-dlp-backed plugins.
    /// </summary>
    internal static DownloaderSearchHit? ParseFlatPlaylistHit(
        string stdout, string artist, string fallbackTitle, Func<string, string> urlFromId)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            return ParseFlatPlaylistHitCore(stdout, artist, fallbackTitle, urlFromId);
        }
        catch (JsonException)
        {
            // Unparseable yt-dlp output is a plugin miss, not a crash.
            return null;
        }
    }

    private static DownloaderSearchHit? ParseFlatPlaylistHitCore(
        string stdout, string artist, string fallbackTitle, Func<string, string> urlFromId)
    {
        using var doc = JsonDocument.Parse(stdout);
        var entry = doc.RootElement.TryGetProperty("entries", out var entries)
            && entries.ValueKind == JsonValueKind.Array
            && entries.GetArrayLength() > 0
            ? entries[0]
            : doc.RootElement.TryGetProperty("url", out _)
                ? doc.RootElement
                : default;

        if (entry.ValueKind == JsonValueKind.Undefined) return null;

        // Flat-playlist entries expose either a full URL (SoundCloud, …) or just an
        // ID (YouTube); prefer the full URL when present.
        var url = entry.TryGetProperty("webpage_url", out var wp) && wp.ValueKind == JsonValueKind.String
            ? wp.GetString()
            : entry.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
        var id = entry.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        var sourceUrl = url ?? (string.IsNullOrEmpty(id) ? null : urlFromId(id!));
        if (string.IsNullOrEmpty(sourceUrl)) return null;

        var hitTitle = (entry.TryGetProperty("title", out var t) ? t.GetString() : null)
                      ?? fallbackTitle;
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
            MatchConfidence: Services.FuzzyMatcher.Confidence(
                artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
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
    /// Writes MELODY_ID and, when the filename or metadata contains an
    /// "Artist - Title" pattern, splits it into proper tags.
    /// Shared by all yt-dlp-backed plugins.
    /// </summary>
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

    internal static string? FindProducedFile(string jsonOutput, string outputDirectory)
    {
        // --print-json's "filename" points at the pre-extraction file (e.g. .webm);
        // the real output is {template}_{id}.mp3. Match by video ID + audio ext.
        string? videoId = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                videoId = id.GetString();
        }
        catch
        {
            // JSON parse failed (e.g. warnings on stdout): fall through.
        }

        if (videoId is not null)
        {
            var byId = Directory.EnumerateFiles(outputDirectory)
                .FirstOrDefault(p =>
                    Path.GetFileName(p).Contains(videoId, StringComparison.Ordinal) &&
                    IsAudioExtension(Path.GetExtension(p)));
            if (byId is not null) return byId;
        }

        // Fallback: newest audio file in the directory.
        return Directory.EnumerateFiles(outputDirectory)
            .Where(p => IsAudioExtension(Path.GetExtension(p)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsAudioExtension(string? ext)
        => ext is not null && ext.ToLowerInvariant() is ".mp3" or ".m4a" or ".opus" or ".ogg" or ".flac" or ".wav" or ".webm";

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
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -v error -i {file} -c copy {target}",
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (process is null) return null;
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)) || process.ExitCode != 0 || !File.Exists(target))
            return null;

        try { File.Delete(file); } catch { /* keep both, harmless */ }
        return target;
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
