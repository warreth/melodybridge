using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// SoundCloud downloader plugin backed by yt-dlp's scsearch extractor.
///
/// SoundCloud serves the artist's original upload (often 320 kbps),
/// so it is tried before YouTube in the default waterfall. The top
/// search results are ranked by match quality instead of trusting the
/// first row, and downloads that report an audio bitrate under
/// 128 kbps are rejected and deleted: no low-quality files enter the
/// library.
/// </summary>
public class SoundCloudDownloader : IDownloader
{
    private readonly ILogger<SoundCloudDownloader> _logger;

    private const int MinimumAudioBitrateKbps = 128;

    // SoundCloud source mp3: 128-320 kbps, lossy only (opus originals count as lossy too).
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Mp3], 128, 320, SupportsLossless: false, SupportsLossy: true);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "soundcloud";
    public string Name => "SoundCloud (original uploads)";
    public string Description => "SoundCloud original uploads, 128 kbps+ only";

    public SoundCloudDownloader(ILogger<SoundCloudDownloader> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(YtDlpProcess.BinaryPath is not null);

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        if (YtDlpProcess.BinaryPath is null) return null;

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        try
        {
            var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
            {
                $"scsearch5:{searchText}",
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
            }, TimeSpan.FromSeconds(30), ct);

            if (exit != 0) return null;

            // Top 5 ranked, never the first row: scsearch's first hit is
            // routinely a cover, a remix or a repost of another artist.
            // SoundCloud does not index ISRCs; the text query is the way in.
            var hits = YtDlpDownloader.ParseFlatPlaylistHits(stdout, query.Artist, query.Title, id => id);
            return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, hits);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoundCloud search failed for '{Query}'", searchText);
            return null;
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
            var template = Path.Combine(outputDirectory, "sc_%(id)s.%(ext)s");
            quality ??= DownloadQuality.Any;

            // Same policy as the YouTube plugin: audio-only, explicit
            // formats transcode to the cap, Auto keeps the source codec.
            var args = YtDlpProcess.BuildDownloadArgs(template, sourceUrl, quality);
            var (exit, stdout, stderr) = await YtDlpProcess.RunDownloadAsync(args, TimeSpan.FromMinutes(5), ct);

            if (exit != 0)
                return new DownloaderDownloadResult(false, null, $"yt-dlp exited {exit}: {Truncate(stderr, 400)}");

            var file = YtDlpDownloader.FindProducedFile(stdout, outputDirectory);
            if (file is null)
                return new DownloaderDownloadResult(false, null, "no output file found");

            // Quality gate: reject low-bitrate rips so bad files never land
            // in the library. A requested band floor above 128 tightens it.
            // A rejected file is deleted, not left for the scanner to find.
            var bitrate = ReadAudioBitrateKbps(stdout);
            var floor = Math.Max(MinimumAudioBitrateKbps, quality?.MinKbps ?? 0);
            if (bitrate < floor)
            {
                try { File.Delete(file); } catch { /* best effort */ }
                return new DownloaderDownloadResult(false, null,
                    $"rejected: audio bitrate {bitrate?.ToString() ?? "unknown"} kbps is below {floor} kbps");
            }

            var trackTitle = ReadJsonString(stdout, "track") ?? ReadJsonString(stdout, "title");
            YtDlpDownloader.TagDownloadedFile(file, expectedTitle: trackTitle);

            return new DownloaderDownloadResult(true, file, null);
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Reads the audio bitrate (kbps) from yt-dlp's JSON. yt-dlp reports
    /// both "abr" and "tbr" in kbps already; "vbr" is the video bitrate
    /// and never a valid audio signal.
    /// </summary>
    internal static int? ReadAudioBitrateKbps(string jsonOutput)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;
            if (root.TryGetProperty("abr", out var abr) && abr.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(abr.GetDouble());
            if (root.TryGetProperty("tbr", out var tbr) && tbr.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(tbr.GetDouble());
        }
        catch { /* parse failure falls through to null */ }
        return null;
    }

    private static string? ReadJsonString(string jsonOutput, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            return doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
