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

    // SoundCloud uploads are mp3 or opus and the plugin rejects files
    // under 128 kbps; an explicit mp3 request transcodes via yt-dlp to any
    // bitrate in 128-320. An Auto request hands back the original upload.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "soundcloud";
    public string Name => "SoundCloud (original uploads)";
    public string Description => "SoundCloud original uploads, 128 kbps+ only";

    /// <inheritdoc />
    public string? LastUnavailableReason { get; private set; }

    public SoundCloudDownloader(ILogger<SoundCloudDownloader> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var ready = YtDlpProcess.BinaryPath is not null;
        LastUnavailableReason = ready ? null : "yt-dlp was not found on PATH";
        return Task.FromResult(ready);
    }

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        if (YtDlpProcess.BinaryPath is null) return null;

        // Top 5 ranked, never the first row: scsearch's first hit is
        // routinely a cover, a remix or a repost of another artist.
        // SoundCloud does not index ISRCs; the text query is the way in.
        var hits = await SearchAllAsync(query, ct);
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, hits);
    }

    /// <summary>
    /// Every parsed hit, unranked: the manual source picker shows what the
    /// ranking refused, so a human can still pick it.
    /// </summary>
    public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return Array.Empty<DownloaderSearchHit>();

        try
        {
            var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
            {
                $"scsearch5:{searchText}",
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
            }, TimeSpan.FromSeconds(30), ct);

            if (exit != 0) return Array.Empty<DownloaderSearchHit>();
            return YtDlpDownloader.ParseFlatPlaylistHits(stdout, query.Artist, query.Title, id => id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoundCloud search failed for '{Query}'", searchText);
            return Array.Empty<DownloaderSearchHit>();
        }
    }

    /// <inheritdoc />
    public Task<CandidateAudioQuality?> ProbeQualityAsync(
        string sourceUrl, CancellationToken ct = default)
        => YtDlpProcess.ProbeAudioQualityAsync(sourceUrl, ct);

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

            // Some SoundCloud uploads arrive as opus inside a .webm, which
            // TagLib cannot tag. Remux losslessly so the MELODY_ID survives.
            file = YtDlpDownloader.RemuxWebmToOpus(file) ?? file;

            // Quality gate: reject low-bitrate rips so bad files never land
            // in the library. A requested band floor above 128 tightens it.
            // A rejected file is deleted, not left for the scanner to find.
            // A stream that reports no bitrate is rejected too: the plugin
            // promises a floor over the bytes it delivers, and an unknown
            // bitrate IS an unverified claim, unlike ArchiveOrg's plugin
            // which deliberately passes unknowns.
            var bitrate = ReadAudioBitrateKbps(stdout);
            var floor = Math.Max(MinimumAudioBitrateKbps, quality?.MinKbps ?? 0);
            if (RejectsByteSource(bitrate, floor))
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

    /// <summary>
    /// The plugin's floor promise: a source that reports no audio bitrate
    /// is an unverified claim, so it is rejected like a bitrate under the
    /// floor rather than handed to the library as known-good.
    /// </summary>
    internal static bool RejectsByteSource(int? bitrate, int floor)
        => bitrate is null || bitrate < floor;

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
