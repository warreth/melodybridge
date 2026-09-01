using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// SoundCloud downloader plugin backed by yt-dlp's scsearch extractor.
///
/// SoundCloud serves the artist's original upload (often 320 kbps),
/// so it is tried before YouTube in the default waterfall. Downloads
/// that report an audio bitrate under 128 kbps are rejected — no
/// low-quality files enter the library.
/// </summary>
public class SoundCloudDownloader : IDownloader
{
    private readonly ILogger<SoundCloudDownloader> _logger;

    private const int MinimumAudioBitrateKbps = 128;

    public string Id => "soundcloud";
    public string Name => "SoundCloud (original uploads)";
    public string Description => "SoundCloud original uploads, 128 kbps+ only";

    public SoundCloudDownloader(ILogger<SoundCloudDownloader> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(YtDlpProcess.BinaryPath is not null);

    public async Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
    {
        if (YtDlpProcess.BinaryPath is null) return null;

        var query = $"{artist} {title}".Trim();
        if (query.Length == 0) return null;

        try
        {
            var (exit, stdout, _) = await YtDlpProcess.RunAsync(new[]
            {
                $"scsearch1:{query}",
                "--flat-playlist",
                "--dump-single-json",
                "--no-warnings",
            }, TimeSpan.FromSeconds(45), ct);

            if (exit != 0) return null;

            return YtDlpDownloader.ParseFlatPlaylistHit(stdout, artist, title, id => id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SoundCloud search failed for '{Query}'", query);
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

            // Same policy as yt-dlp: Auto keeps the source codec (SoundCloud
            // originals, often opus/m4a); explicit formats transcode to the cap.
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

            var file = YtDlpDownloader.FindProducedFile(stdout, outputDirectory);
            if (file is null)
                return new DownloaderDownloadResult(false, null, "no output file found");

            // Quality gate: reject low-bitrate rips so bad files never land
            // in the library. A requested band floor above 128 tightens it.
            var bitrate = ReadAudioBitrateKbps(stdout);
            var floor = Math.Max(MinimumAudioBitrateKbps, quality?.MinKbps ?? 0);
            if (bitrate < floor)
                return new DownloaderDownloadResult(false, null,
                    $"rejected: audio bitrate {bitrate ?? 0} kbps is below {floor} kbps");

            var trackTitle = ReadJsonString(stdout, "track") ?? ReadJsonString(stdout, "title");
            YtDlpDownloader.TagDownloadedFile(file, melodyId, expectedTitle: trackTitle);

            return new DownloaderDownloadResult(true, file, null);
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>Reads the abr/vbr bitrate (bps) from yt-dlp JSON into kbps.</summary>
    private static int? ReadAudioBitrateKbps(string jsonOutput)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;
            if (root.TryGetProperty("abr", out var abr) && abr.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(abr.GetDouble());
            if (root.TryGetProperty("vbr", out var vbr) && vbr.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(vbr.GetDouble());
            if (root.TryGetProperty("tbr", out var tbr) && tbr.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(tbr.GetDouble() / 1000);
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
