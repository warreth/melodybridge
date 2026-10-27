using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.FlacMusic;

/// <summary>
/// The two public endpoints behind flac.music.hi.cn: a POST search that
/// returns Kuwo or NetEase tracks with their available formats, and a POST
/// getUrl that turns a search result plus its short-lived sign into a direct
/// audio URL. The site is a Chinese music search front-end, not a catalogue
/// with ISRCs, so this client only ever searches by text.
/// </summary>
public class FlacMusicClient
{
    public const string DefaultBaseUrl = "https://flac.music.hi.cn";

    public enum Status
    {
        Ok,
        /// <summary>getUrl's sign or timestamp expired; search again.</summary>
        Expired,
        /// <summary>The site answered with an error code or malformed data.</summary>
        Failed,
        /// <summary>The site did not answer.</summary>
        Unreachable,
    }

    public record Format(string Name, int BitrateKbps, long? SizeBytes = null)
    {
        public bool IsFlac => Name.Equals("flac", StringComparison.OrdinalIgnoreCase);
    }

    public record Track(
        string Platform,
        string Id,
        string Title,
        string Artist,
        string? Album,
        TimeSpan? Duration,
        IReadOnlyList<Format> Formats,
        string? Time,
        string? Sign,
        string? Link,
        string? CoverUrl);

    public record SearchResult(
        IReadOnlyList<Track> Tracks,
        Status Status,
        string? Message = null)
    {
        public static readonly IReadOnlyList<Track> Empty = Array.Empty<Track>();
    }

    /// <summary>
    /// A getUrl answer. <paramref name="Format"/> and
    /// <paramref name="BitrateKbps"/> are what the CDN will really serve,
    /// which is not always what the search row promised: some Kuwo 128
    /// rows come back as AAC/100 while labelled mp3.
    /// </summary>
    public record UrlResult(
        Status Status,
        string? Url,
        string? Format = null,
        int? BitrateKbps = null,
        string? Message = null);

    private readonly HttpClient _http;
    private readonly ILogger<FlacMusicClient> _logger;
    private string _baseUrl = DefaultBaseUrl;

    public FlacMusicClient(HttpClient http, ILogger<FlacMusicClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = string.IsNullOrWhiteSpace(value)
            ? DefaultBaseUrl
            : value.Trim().TrimEnd('/');
    }

    /// <summary>A tiny page request: the site answers even when both platform searches are empty.</summary>
    public async Task<(bool Ready, string? Reason)> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await _http.GetAsync(_baseUrl + "/", budget.Token);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, $"flac.music.hi.cn answered HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flac.music.hi.cn health probe failed");
            return (false, "flac.music.hi.cn did not answer");
        }
    }

    public async Task<SearchResult> SearchAsync(
        string platform, string keyword, int size = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return new(SearchResult.Empty, Status.Ok);

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["platform"] = platform,
                ["keyword"] = keyword,
                ["page"] = "1",
                ["size"] = Math.Clamp(size, 1, 50).ToString(),
            });
            using var response = await _http.PostAsync(
                _baseUrl + "/ajax.php?act=search", content, ct);
            if (!response.IsSuccessStatusCode)
                return new(SearchResult.Empty, Status.Unreachable,
                    $"flac.music.hi.cn search answered HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseSearch(body, platform);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flac.music.hi.cn search '{Query}' failed", keyword);
            return new(SearchResult.Empty, Status.Unreachable,
                "flac.music.hi.cn did not answer");
        }
    }

    /// <summary>
    /// One track by platform and id. The site's song endpoint returns the
    /// same item shape as a search, with a fresh sign, so a direct Kuwo or
    /// NetEase link and an expired encoded URL can both be resolved without
    /// a text search.
    /// </summary>
    public async Task<SearchResult> GetTrackAsync(
        string platform, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(id))
            return new(SearchResult.Empty, Status.Failed, "missing platform or song id");

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["platform"] = platform,
                ["id"] = id,
                ["page"] = "1",
                ["size"] = "1",
            });
            using var response = await _http.PostAsync(
                _baseUrl + "/ajax.php?act=song", content, ct);
            if (!response.IsSuccessStatusCode)
                return new(SearchResult.Empty, Status.Unreachable,
                    $"flac.music.hi.cn song answered HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseSearch(body, platform);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flac.music.hi.cn song {Platform}/{Id} failed", platform, id);
            return new(SearchResult.Empty, Status.Unreachable,
                "flac.music.hi.cn did not answer");
        }
    }

    /// <summary>
    /// Turns one search result into a direct URL. The site's sign is short
    /// lived, so an expired answer is reported separately and the caller
    /// can fetch a fresh pair by id.
    /// </summary>
    public async Task<UrlResult> GetUrlAsync(
        Track track, Format format, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.Time) || string.IsNullOrWhiteSpace(track.Sign))
            return new(Status.Expired, null,
                Message: "the search result carried no sign; search again");

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["platform"] = track.Platform,
                ["songid"] = track.Id,
                ["format"] = format.Name,
                ["bitrate"] = format.BitrateKbps.ToString(),
                ["time"] = track.Time,
                ["sign"] = track.Sign,
            });
            using var response = await _http.PostAsync(
                _baseUrl + "/ajax.php?act=getUrl", content, ct);
            if (!response.IsSuccessStatusCode)
                return new(Status.Unreachable, null,
                    Message: $"flac.music.hi.cn getUrl answered HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseGetUrl(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flac.music.hi.cn getUrl for {Platform}/{Id} failed",
                track.Platform, track.Id);
            return new(Status.Unreachable, null, Message: "flac.music.hi.cn did not answer");
        }
    }

    /// <summary>Streams one direct audio URL into a local file.</summary>
    public async Task<(bool Ok, string? Error)> DownloadFileAsync(
        string url, string path, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"the audio CDN answered HTTP {(int)response.StatusCode}");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, 81920, ct);
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "flac.music.hi.cn audio download failed");
            return (false, ex.Message);
        }
    }

    /// <summary>Parses a search response. Internal for tests.</summary>
    internal static SearchResult ParseSearch(string json, string platform)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : -1;
            if (code != 0)
                return new(SearchResult.Empty, Status.Failed,
                    GetString(root, "msg") ?? $"flac.music.hi.cn answered code {code}");

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array)
                return new(SearchResult.Empty, Status.Ok);

            var tracks = new List<Track>();
            foreach (var item in list.EnumerateArray())
            {
                var id = GetId(item);
                var title = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

                var duration = item.TryGetProperty("duration", out var d)
                    ? ParseSeconds(d)
                    : null;
                var formats = new List<Format>();
                if (item.TryGetProperty("minfo", out var minfo) && minfo.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in minfo.EnumerateArray())
                    {
                        var name = GetString(entry, "format");
                        var bitrate = entry.TryGetProperty("bitrate", out var b)
                            ? ParseInt(b)
                            : null;
                        if (string.IsNullOrWhiteSpace(name) || bitrate is null or <= 0) continue;
                        var size = entry.TryGetProperty("size", out var s)
                            ? ParseSizeBytes(s)
                            : null;
                        formats.Add(new Format(name!.ToLowerInvariant(), bitrate.Value, size));
                    }
                }
                if (formats.Count == 0) continue;

                tracks.Add(new Track(
                    Platform: platform,
                    Id: id!,
                    Title: title!,
                    Artist: GetString(item, "artist") ?? "",
                    Album: GetString(item, "album_name"),
                    Duration: duration,
                    Formats: formats,
                    Time: GetStringOrNumber(item, "time"),
                    Sign: GetString(item, "sign"),
                    Link: GetString(item, "link"),
                    CoverUrl: GetString(item, "pic_url")));
            }
            return new(tracks, Status.Ok);
        }
        catch (JsonException)
        {
            return new(SearchResult.Empty, Status.Failed,
                "flac.music.hi.cn returned invalid JSON");
        }
    }

    /// <summary>Parses a getUrl response. Internal for tests.</summary>
    internal static UrlResult ParseGetUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : -1;
            if (code == -3)
                return new(Status.Expired, null,
                    Message: GetString(root, "msg") ?? "the sign expired");
            if (code != 0)
                return new(Status.Failed, null,
                    Message: GetString(root, "msg") ?? $"flac.music.hi.cn answered code {code}");

            if (!root.TryGetProperty("data", out var data))
                return new(Status.Failed, null,
                    Message: "flac.music.hi.cn returned no download data");
            var url = GetString(data, "url");
            if (string.IsNullOrWhiteSpace(url))
                return new(Status.Failed, null,
                    Message: "flac.music.hi.cn returned an empty download URL");
            return new(Status.Ok, url,
                GetString(data, "format"),
                ParseIntValue(data, "bitrate"));
        }
        catch (JsonException)
        {
            return new(Status.Failed, null,
                Message: "flac.music.hi.cn returned invalid getUrl JSON");
        }
    }

    private static int? ParseIntValue(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
            ? ParseInt(value)
            : null;

    private static string? GetId(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.ToString(),
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Some site fields are numbers on one platform and strings on the
    /// other (time is a Unix second number on Kuwo, a string on NetEase).
    /// </summary>
    private static string? GetStringOrNumber(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? ParseInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static TimeSpan? ParseSeconds(JsonElement value)
    {
        var seconds = ParseInt(value);
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
    }

    /// <summary>"31.08Mb" or "31.08MB" -> bytes; null when unreadable.</summary>
    internal static long? ParseSizeBytes(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = text.Trim();
        var multiplier = 1L;
        if (normalized.EndsWith("GB", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("G", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024L * 1024 * 1024;
            normalized = normalized.TrimEnd('B', 'b', 'G', 'g');
        }
        else if (normalized.EndsWith("MB", StringComparison.OrdinalIgnoreCase)
                 || normalized.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024L * 1024;
            normalized = normalized.TrimEnd('B', 'b', 'M', 'm');
        }
        else if (normalized.EndsWith("KB", StringComparison.OrdinalIgnoreCase)
                 || normalized.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024L;
            normalized = normalized.TrimEnd('B', 'b', 'K', 'k');
        }

        return double.TryParse(normalized, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? (long)(number * multiplier)
            : null;
    }

    /// <summary>Not used yet, but keep the response shape honest if the site adds a challenge.</summary>
    internal static HttpStatusCode StatusOf(HttpResponseMessage response) => response.StatusCode;
}
