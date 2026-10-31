using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Internet Archive downloader plugin (archive.org).
///
/// ArchiveOrgSearchProvider searches the public advancedsearch + metadata
/// APIs and ranks the MP3 files inside the matching items. This part streams
/// the chosen file from the archive servers. No account and no captcha. The
/// Archive hosts vast numbers of community-uploaded and public-domain music
/// MP3s (78rpm digitizations, live music, netlabels).
/// </summary>
public class ArchiveOrgDownloader : IDownloader
{
    private readonly ILogger<ArchiveOrgDownloader> _logger;
    private readonly HttpClient _http;

    public string Id => "archiveorg";
    public string Name => "Internet Archive";
    public string Description => "archive.org public recordings and digitizations, MP3";

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Text,
            "Internet Archive title search");

    public string IconName => "server";
    public IReadOnlyList<string> Notes => new[] { "public-domain recordings" };

    /// <inheritdoc />
    public string? LastUnavailableReason { get; private set; }

    // The waterfall probes every enabled plugin per track; the archive is
    // not hammered by a status sweep.
    private readonly AvailabilityCache _availability = new();

    // Archive files are MP3 uploads; files under 128 kbps are rejected
    // after the download. The band is selected from the file metadata.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public ArchiveOrgDownloader(HttpClient http, ILogger<ArchiveOrgDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        // A real probe, not an unconditional true: the archive answers 5xx
        // under load and claiming ready while it is down costs every track
        // an attempt. One tiny search request is cheap enough to cache.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var available = false;
        try
        {
            using var resp = await _http.GetAsync(
                new Uri($"{ArchiveOrgApi.SearchBase}?q=melodybridge&fl[]=identifier&rows=1&output=json"), cts.Token);
            available = resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "archive.org availability probe failed");
        }
        LastUnavailableReason = available ? null : "archive.org did not answer";
        _availability.Store(available);
        return available;
    }

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        try
        {
            // sourceUrl is a direct archive.org download URL (from our own search hit).
            if (!sourceUrl.Contains("archive.org/download/", StringComparison.OrdinalIgnoreCase))
                return new DownloaderDownloadResult(false, null, "not an archive.org download URL");

            Directory.CreateDirectory(outputDirectory);

            var fileName = Uri.UnescapeDataString(sourceUrl[(sourceUrl.LastIndexOf('/') + 1)..]);
            var target = Path.Combine(outputDirectory, fileName);
            if (!target.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                target += ".mp3";

            // Item file names are not unique across items ("01 - Intro.mp3"
            // lives in thousands of albums): never overwrite a file that
            // belongs to another track.
            if (File.Exists(target))
                target = Path.Combine(outputDirectory,
                    $"{Path.GetFileNameWithoutExtension(target)}_{ShortHash(sourceUrl)}{Path.GetExtension(target)}");

            // A 5xx from the archive is usually transient load; retry it
            // briefly instead of losing the track (the live suite sees
            // these sporadically). 4xx stays a hard verdict.
            using var resp = await HttpRetry.SendAsync(
                token => _http.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, token),
                attempts: 3,
                initialDelay: TimeSpan.FromMilliseconds(500),
                ct: ct);
            if (!resp.IsSuccessStatusCode)
                return new DownloaderDownloadResult(false, null, $"archive.org returned {(int)resp.StatusCode}");

            try
            {
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(target))
                {
                    await src.CopyToAsync(dst, 81920, ct);
                }
            }
            catch
            {
                try { File.Delete(target); } catch { /* best effort */ }
                throw;
            }

            // An error page served under a 200 is not an MP3; sniff the bytes.
            var container = Audio.AudioSignature.EnsureExtension(target);
            if (container is null || !container.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(container ?? target); } catch { /* best effort */ }
                return new DownloaderDownloadResult(false, null, "archive.org returned a file that is not MP3 audio");
            }
            target = container;

            // Quality gate: only keep real MP3s of decent bitrate.
            var bitrate = ProbeBitrateKbps(target);
            if (bitrate is < 128)
            {
                try { File.Delete(target); } catch { /* best effort */ }
                return new DownloaderDownloadResult(false, null,
                    $"rejected: {bitrate?.ToString() ?? "unknown"} kbps is below 128 kbps");
            }

            var embeddedTitle = Path.GetFileNameWithoutExtension(fileName);
            var sep = embeddedTitle.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
                Tagging.TaglibHelper.WriteTags(target,
                    title: embeddedTitle[(sep + 3)..].Trim(),
                    artist: embeddedTitle[..sep].Trim());
            return new DownloaderDownloadResult(true, target, null);
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>Stable short hash for filename disambiguation.</summary>
    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>Reads the bitrate of a local MP3 via TagLib properties.</summary>
    private static int? ProbeBitrateKbps(string file)
    {
        try
        {
            using var tf = TagLib.File.Create(file);
            return tf.Properties?.AudioBitrate > 0 ? tf.Properties.AudioBitrate : null;
        }
        catch { return null; }
    }


}
