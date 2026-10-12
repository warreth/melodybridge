using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
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

    // Archive files are MP3 uploads, never above 320 kbps, lossy by container.
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Mp3], null, 320, SupportsLossless: false, SupportsLossy: true);
    public PluginCapabilities Capabilities => Caps;

    public ArchiveOrgDownloader(HttpClient http, ILogger<ArchiveOrgDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(true); // public API, always up unless the network is down

    public async Task<DownloaderSearchHit?> SearchAsync(
        string artist, string title, DownloadQuality quality,
        TimeSpan? expectedDuration = null, CancellationToken ct = default)
    {
        var query = SearchTerms.Query(artist, title);
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

            // Collect candidates from several items (an item usually holds
            // a whole album): the right track may be the third item or the
            // eighth file inside one, so nothing is trusted by position.
            var candidates = new List<DownloaderSearchHit>();
            foreach (var docItem in docs.EnumerateArray().Take(3))
            {
                var identifier = GetString(docItem, "identifier");
                if (string.IsNullOrEmpty(identifier)) continue;

                candidates.AddRange(await FindMp3CandidatesAsync(identifier!, artist, title, ct));
            }

            // The band is checked before ranking: a FLAC-grade master in
            // the wrong bitrate must not win over a fitting file.
            return SearchRanker.Rank(artist, title, expectedDuration,
                candidates.Where(c => quality.IsWithinBand(c.BitrateKbps)));
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

            // Item file names are not unique across items ("01 - Intro.mp3"
            // lives in thousands of albums): never overwrite a file that
            // belongs to another track.
            if (File.Exists(target))
                target = Path.Combine(outputDirectory,
                    $"{Path.GetFileNameWithoutExtension(target)}_{ShortHash(sourceUrl)}{Path.GetExtension(target)}");

            using var resp = await _http.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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

    /// <summary>
    /// Every MP3 file inside one archive item as a candidate hit: an item
    /// holds a whole album, so the requested track is usually not the
    /// first file. Ranking happens across all items and files.
    /// </summary>
    private async Task<List<DownloaderSearchHit>> FindMp3CandidatesAsync(
        string identifier, string artist, string fallbackTitle, CancellationToken ct)
    {
        var hits = new List<DownloaderSearchHit>();
        try
        {
            using var resp = await _http.GetAsync($"https://archive.org/metadata/{identifier}", ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return hits;

            var itemTitle = GetMetadataTitle(doc.RootElement);
            var creator = GetMetadataCreator(doc.RootElement);
            foreach (var f in files.EnumerateArray())
            {
                var name = GetString(f, "name");
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip preview-like fragments when the length is known.
                var duration = GetDurationSeconds(GetString(f, "length"));
                if (duration is { TotalSeconds: < 20 }) continue;

                // Canonical archive.org URL: redirects to the correct storage node.
                var url = $"https://archive.org/download/{identifier}/{Uri.EscapeDataString(name)}";

                var fileTitle = GetString(f, "title") ?? TitleFromFileName(name) ?? itemTitle ?? fallbackTitle;
                var fileArtist = creator ?? artist;
                hits.Add(new DownloaderSearchHit(
                    Title: fileTitle,
                    Artist: fileArtist,
                    SourceUrl: url,
                    Duration: duration,
                    BitrateKbps: TryGetInt(f, "bitrate"),
                    MatchConfidence: Services.FuzzyMatcher.Confidence(
                        artist, fallbackTitle, fileArtist, fileTitle)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Archive.org metadata lookup failed for {Identifier}", identifier);
        }
        return hits;
    }

    /// <summary>"Artist - Title.mp3" file names: the readable title inside.</summary>
    private static string? TitleFromFileName(string name)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(name);
        var sep = withoutExtension.IndexOf(" - ", StringComparison.Ordinal);
        return sep > 0 ? withoutExtension[(sep + 3)..].Trim() : withoutExtension;
    }

    /// <summary>Item creator, string or array (archive.org stores both).</summary>
    private static string? GetMetadataCreator(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;
        if (!meta.TryGetProperty("creator", out var c))
            return null;
        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString(),
            JsonValueKind.Array when c.GetArrayLength() > 0 && c[0].ValueKind == JsonValueKind.String
                => c[0].GetString(),
            _ => null,
        };
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

    /// <summary>Archive file facts arrive as numbers or numeric strings.</summary>
    private static int? TryGetInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
