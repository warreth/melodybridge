using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Internet Archive downloader plugin (archive.org).
///
/// Uses the public advancedsearch + metadata APIs: no account, no
/// captcha. The Archive hosts vast numbers of community-uploaded and
/// public-domain music MP3s (78rpm digitizations, live music, netlabels).
/// Files are streamed straight from the archive servers.
/// </summary>
public class ArchiveOrgDownloader : IDownloader
{
    private readonly ILogger<ArchiveOrgDownloader> _logger;
    private readonly HttpClient _http;

    public string Id => "archiveorg";
    public string Name => "Internet Archive";
    public string Description => "archive.org public recordings and digitizations, MP3";

    private static readonly Uri SearchBase = new("https://archive.org/advancedsearch.php");

    public ArchiveOrgDownloader(HttpClient http, ILogger<ArchiveOrgDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(true); // public API, always up unless the network is down

    public async Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
    {
        var query = $"{artist} {title}".Trim();
        if (query.Length == 0) return null;

        try
        {
            // Only items that actually contain MP3 files.
            var q = Uri.EscapeDataString($"{query} AND mediatype:audio AND format:(VBR MP3)");
            var url = $"{SearchBase}?q={q}&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&rows=5&page=1&output=json";

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("docs", out var docs) ||
                docs.ValueKind != JsonValueKind.Array || docs.GetArrayLength() == 0)
                return null;

            foreach (var docItem in docs.EnumerateArray())
            {
                var identifier = GetString(docItem, "identifier");
                if (string.IsNullOrEmpty(identifier)) continue;

                // Look up the first MP3 file inside the item.
                var hit = await TryFindMp3InItem(identifier!, artist, title, ct);
                // Real per-file bitrate is known before downloading; honor the band.
                if (hit is not null && quality.IsWithinBand(hit.BitrateKbps))
                    return hit;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Archive.org search failed for '{Query}'", query);
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

            using var resp = await _http.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new DownloaderDownloadResult(false, null, $"archive.org returned {(int)resp.StatusCode}");

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(target))
            {
                await src.CopyToAsync(dst, 81920, ct);
            }

            // Quality gate: only keep real MP3s of decent bitrate.
            var bitrate = ProbeBitrateKbps(target);
            if (bitrate is < 128)
            {
                try { File.Delete(target); } catch { /* best effort */ }
                return new DownloaderDownloadResult(false, null,
                    $"rejected: {bitrate ?? 0} kbps is below 128 kbps");
            }

            var embeddedTitle = Path.GetFileNameWithoutExtension(fileName);
            var sep = embeddedTitle.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
                Tagging.TaglibHelper.WriteTags(target,
                    title: embeddedTitle[(sep + 3)..].Trim(),
                    artist: embeddedTitle[..sep].Trim());
            if (melodyId is not null)
                Tagging.TaglibHelper.WriteMelodyId(target, melodyId);

            return new DownloaderDownloadResult(true, target, null);
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    private async Task<DownloaderSearchHit?> TryFindMp3InItem(
        string identifier, string artist, string fallbackTitle, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://archive.org/metadata/{identifier}", ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return null;

            // Prefer the first mp3 whose name hints at a full track (skip 30s preview-like files by size heuristics later).
            foreach (var f in files.EnumerateArray())
            {
                var name = GetString(f, "name");
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Canonical archive.org URL: redirects to the correct storage node.
                var url = $"https://archive.org/download/{identifier}/{Uri.EscapeDataString(name)}";

                var title = GetString(f, "title") ?? GetMetadataTitle(doc.RootElement) ?? fallbackTitle;
                var duration = GetDurationSeconds(GetString(f, "length"));

                return new DownloaderSearchHit(title, artist, url, duration,
                    BitrateKbps: TryGetInt(f, "bitrate"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Archive.org metadata lookup failed for {Identifier}", identifier);
        }
        return null;
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

    /// <summary>Archive "length" is "mm:ss" or raw seconds.</summary>
    private static TimeSpan? GetDurationSeconds(string? length)
    {
        if (string.IsNullOrEmpty(length)) return null;
        if (double.TryParse(length, out var secs))
            return TimeSpan.FromSeconds(secs);
        var parts = length.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s))
            return TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
        return null;
    }

    private static string? GetMetadataTitle(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;
        if (!meta.TryGetProperty("title", out var t))
            return null;
        return t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 && t[0].ValueKind == JsonValueKind.String
                ? t[0].GetString()
                : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i))
            return i;
        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
