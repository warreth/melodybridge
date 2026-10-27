using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Rhythmax;

/// <summary>
/// The public Rhythmax hifi API behind tidal.odskyler.com. The frontend
/// obtains a short-lived playback token from /token, asks /manifests for a
/// track, and receives a Tidal DASH manifest. This client does the same,
/// then downloads the init segment and every media segment and remuxes the
/// result into the container the caller asked for. No Tidal account is
/// involved; the service's own accounts own playback.
///
/// The service is deliberately treated as best effort: its account pool
/// flips between FULL, PREVIEW and DEAD, returns 202 while a playback
/// request is queued, and rate limits. Every one of those becomes a short
/// status message, never a thrown exception.
/// </summary>
public class RhythmaxClient
{
    public const string DefaultBaseUrl = "https://hifi.odskyler.com";

    public enum ManifestStatus
    {
        Ok,
        /// <summary>The service only had a 30 second preview for this track.</summary>
        Preview,
        /// <summary>The service asked us to slow down or has no playback account.</summary>
        RateLimited,
        /// <summary>The service did not answer or returned a malformed body.</summary>
        Unreachable,
    }

    public record ManifestResult(
        ManifestStatus Status,
        string? ManifestUrl = null,
        string? Presentation = null,
        IReadOnlyList<string>? Formats = null,
        string? Message = null,
        TimeSpan? RetryAfter = null);

    /// <summary>A byte range for a SegmentBase representation; a null length runs to EOF.</summary>
    public record ByteRange(long Start, long? Length = null)
    {
        public string Header => Length is { } length
            ? $"bytes={Start}-{Start + length - 1}"
            : $"bytes={Start}-";
    }

    /// <summary>One DASH representation reduced to the URLs needed to download it.</summary>
    public record DashPlan(
        string InitializationUrl,
        IReadOnlyList<string> SegmentUrls,
        string Codecs,
        string MimeType,
        string? Label,
        ByteRange? InitializationRange = null,
        ByteRange? MediaRange = null);

    /// <summary>What one successful download produced.</summary>
    public record DownloadResult(string FilePath, string Codecs);

    private readonly HttpClient _http;
    private readonly ILogger<RhythmaxClient> _logger;

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public RhythmaxClient(HttpClient http, ILogger<RhythmaxClient> logger)
        : this(http, DefaultBaseUrl, logger)
    {
    }

    public RhythmaxClient(HttpClient http, string baseUrl, ILogger<RhythmaxClient> logger)
    {
        _http = http;
        _baseUrl = NormalizeBase(baseUrl);
        _logger = logger;
    }

    private string _baseUrl;

    /// <summary>
    /// The service root. The downloader points this at the configured
    /// mirror before each call; a mirror change invalidates the cached
    /// token because the token belongs to the host.
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            var next = NormalizeBase(value);
            if (string.Equals(next, _baseUrl, StringComparison.Ordinal)) return;
            _baseUrl = next;
            _token = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private static string NormalizeBase(string? value)
        => string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim().TrimEnd('/');

    // ── Availability ─────────────────────────────────────────────────────

    /// <summary>
    /// The service's own health summary. FULL is ready to deliver a full
    /// track; PREVIEW can only produce 30 second clips, which would fail the
    /// quality gate, so it is just as unavailable as DEAD.
    /// </summary>
    public async Task<(bool Ready, string? Reason)> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await _http.GetAsync(_baseUrl + "/health", budget.Token);
            if (!response.IsSuccessStatusCode)
                return (false, $"Rhythmax health answered HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(budget.Token));
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";
            var active = root.TryGetProperty("active", out var a)
                && a.ValueKind == JsonValueKind.Number
                && a.TryGetInt32(out var count)
                ? count
                : (int?)null;

            if (string.Equals(status, "FULL", StringComparison.OrdinalIgnoreCase))
                return (true, null);

            var reason = status.ToUpperInvariant() switch
            {
                "PREVIEW" => "Rhythmax only has preview accounts right now",
                "DEAD" => "Rhythmax has no working Tidal account right now",
                _ when active is 0 => "Rhythmax has no active Tidal account right now",
                _ => "Rhythmax is not ready to deliver full tracks",
            };
            return (false, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The health probe has its own six second budget; a slow
            // service is unavailable, not a caller cancellation.
            return (false, "Rhythmax did not answer in time");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rhythmax health probe failed");
            return (false, "Rhythmax did not answer: " + Trim(ex.Message, 160));
        }
    }

    // ── Manifest ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asks for a full-track manifest at the given Tidal quality
    /// (LOSSLESS, HIGH or LOW). Handles the 202 playback queue the API uses
    /// when every account is busy, and a 401 by refreshing its own token
    /// once.
    /// </summary>
    public async Task<ManifestResult> GetManifestAsync(
        long trackId, string quality, CancellationToken ct = default)
    {
        try
        {
            var token = await TokenAsync(forceRefresh: false, ct);
            if (token is null)
                return new(ManifestStatus.Unreachable,
                    Message: "Rhythmax did not hand out a playback token");

            var url = $"{_baseUrl}/manifests?id={trackId}&quality={Uri.EscapeDataString(quality)}";
            var result = await RequestManifestAsync(url, token, ct);
            if (result.Status == ManifestStatus.Unreachable
                && result.Message?.Contains("401", StringComparison.Ordinal) == true)
            {
                token = await TokenAsync(forceRefresh: true, ct);
                if (token is null)
                    return new(ManifestStatus.Unreachable,
                        Message: "Rhythmax rejected the playback token");
                result = await RequestManifestAsync(url, token, ct);
            }

            // A 429 is the shared playback pool pacing itself, not a bad
            // request. Honor Retry-After when the API sends it, then back
            // off once or twice before the waterfall moves on.
            for (var attempt = 0;
                 result.Status == ManifestStatus.RateLimited && attempt < 2;
                 attempt++)
            {
                var delay = result.RetryAfter
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                delay = TimeSpan.FromMilliseconds(
                    Math.Clamp(delay.TotalMilliseconds, 100, 10_000));
                _logger.LogDebug(
                    "Rhythmax rate limited track {TrackId}; retrying in {Delay:0.##}s",
                    trackId, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                result = await RequestManifestAsync(url, token, ct);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rhythmax manifest request for {TrackId} failed", trackId);
            return new(ManifestStatus.Unreachable, Message: ex.Message);
        }
    }

    private async Task<ManifestResult> RequestManifestAsync(
        string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Accepted)
            return await PollQueueAsync(response, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(ManifestStatus.Unreachable,
                Message: "Rhythmax token rejected (401)");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(ManifestStatus.RateLimited,
                Message: "Rhythmax rate limited the request; try again later",
                RetryAfter: RetryAfter(response));

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeBodyAsync(response, ct);
            var status = (int)response.StatusCode;
            if (body.Contains("429", StringComparison.Ordinal))
                return new(ManifestStatus.RateLimited,
                    Message: "Rhythmax has no playback slot right now (Tidal 429)",
                    RetryAfter: RetryAfter(response));
            return new(ManifestStatus.Unreachable,
                Message: string.IsNullOrWhiteSpace(body)
                    ? $"Rhythmax answered HTTP {status}"
                    : $"Rhythmax answered HTTP {status}: {Trim(body)}");
        }

        return ParseManifest(await response.Content.ReadAsStringAsync(ct));
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }

    private async Task<ManifestResult> PollQueueAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await SafeBodyAsync(response, ct);
        string? statusUrl = null;
        var retry = 1;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("statusUrl", out var s) && s.ValueKind == JsonValueKind.String)
                statusUrl = s.GetString();
            if (root.TryGetProperty("retryAfter", out var r) && r.ValueKind == JsonValueKind.Number)
                retry = Math.Clamp(r.GetInt32(), 1, 10);
        }
        catch (JsonException)
        {
            // The Location header below is enough.
        }

        statusUrl ??= response.Headers.Location is { } location
            ? location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(_baseUrl), location).ToString()
            : null;
        if (statusUrl is null)
            return new(ManifestStatus.Unreachable,
                Message: "Rhythmax queued the request without a status URL");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(retry), ct);
            using var poll = await _http.GetAsync(statusUrl, ct);
            if (poll.StatusCode == HttpStatusCode.Accepted)
            {
                retry = (int)Math.Min(10, Math.Max(1, poll.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1));
                continue;
            }
            if (!poll.IsSuccessStatusCode)
                return new(ManifestStatus.Unreachable,
                    Message: $"Rhythmax queue status answered HTTP {(int)poll.StatusCode}");
            return ParseManifest(await poll.Content.ReadAsStringAsync(ct));
        }

        return new(ManifestStatus.RateLimited,
            Message: "Rhythmax kept the playback request queued for over a minute");
    }

    /// <summary>
    /// Reads both API shapes in the wild: the nested Tidal OpenAPI envelope
    /// (data.data.attributes) and the custom fork's data.attributes.
    /// Internal for tests.
    /// </summary>
    internal static ManifestResult ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new(ManifestStatus.Unreachable, Message: "Rhythmax returned no manifest data");

            if (data.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object)
                data = nested;
            if (!data.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
                return new(ManifestStatus.Unreachable, Message: "Rhythmax returned no manifest attributes");

            var presentation = attributes.TryGetProperty("trackPresentation", out var p)
                && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            var uri = attributes.TryGetProperty("uri", out var u)
                && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            var formats = new List<string>();
            if (attributes.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var entry in f.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } value)
                        formats.Add(value);

            if (string.IsNullOrWhiteSpace(uri))
                return new(ManifestStatus.Unreachable, Presentation: presentation,
                    Message: "Rhythmax returned a manifest without a URL");

            // Tidal always names the presentation. An API that omits it is
            // treated as full rather than guessed into a preview; the
            // post-download length check catches a short file anyway.
            if (presentation is { Length: > 0 }
                && !string.Equals(presentation, "FULL", StringComparison.OrdinalIgnoreCase))
                return new(ManifestStatus.Preview, Presentation: presentation, Formats: formats,
                    Message: $"Rhythmax only offered a {presentation.ToLowerInvariant()} track");

            return new(ManifestStatus.Ok, uri, presentation, formats);
        }
        catch (JsonException)
        {
            return new(ManifestStatus.Unreachable, Message: "Rhythmax returned invalid JSON");
        }
    }

    // ── DASH ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the DASH manifest into init and media URLs. Tidal uses one
    /// audio Representation with a SegmentTemplate and a SegmentTimeline;
    /// the requested codec picks the representation when an adaptive
    /// manifest carries more than one.
    /// </summary>
    public async Task<DashPlan?> ParseDashAsync(
        string manifestUrl, string requestedFormat, CancellationToken ct = default)
    {
        string xml;
        try
        {
            using var response = await _http.GetAsync(manifestUrl,
                HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Rhythmax DASH manifest answered {Status}", (int)response.StatusCode);
                return null;
            }
            xml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rhythmax DASH manifest could not be read");
            return null;
        }

        return ParseDash(xml, manifestUrl, requestedFormat);
    }

    /// <summary>Pure DASH parser. Internal for tests.</summary>
    internal static DashPlan? ParseDash(string xml, string manifestUrl, string requestedFormat)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        var absolute = new Uri(manifestUrl);
        // A multi-period manifest can start with video or a silent period.
        // Walk periods in order and return the first audio representation
        // that matches the requested codec.
        var periods = document.Descendants()
            .Where(element => element.Name.LocalName == "Period")
            .ToList();
        var scopes = periods.Count > 0
            ? periods.Cast<XElement>()
            : new[] { document.Root! };

        foreach (var scope in scopes)
        {
            var representations = scope.Descendants()
                .Where(element => element.Name.LocalName == "Representation")
                .OrderByDescending(element => CodecScore(Codec(element), requestedFormat))
                .ThenBy(element => element.Attribute("bandwidth")?.Value ?? "");
            foreach (var representation in representations)
            {
                if (!CodecMatches(Codec(representation), requestedFormat)) continue;
                var plan = BuildPlan(representation, absolute);
                if (plan is not null) return plan;
            }
        }

        return null;
    }

    private static DashPlan? BuildPlan(XElement representation, Uri absolute)
    {
        var codecs = Codec(representation);
        var template = representation.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SegmentTemplate")
            ?? representation.Parent?
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SegmentTemplate");
        if (template is not null)
            return BuildSegmentTemplatePlan(representation, template, absolute, codecs);

        var list = representation.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SegmentList")
            ?? representation.Parent?
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SegmentList");
        if (list is not null)
            return BuildSegmentListPlan(representation, list, absolute, codecs);

        var segmentBase = representation.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SegmentBase")
            ?? representation.Parent?
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "SegmentBase");
        if (segmentBase is not null)
            return BuildSegmentBasePlan(representation, segmentBase, absolute, codecs);

        return null;
    }

    private static DashPlan? BuildSegmentTemplatePlan(
        XElement representation, XElement template, Uri absolute, string codecs)
    {
        var init = template.Attribute("initialization")?.Value;
        var media = template.Attribute("media")?.Value;
        if (string.IsNullOrWhiteSpace(init) || string.IsNullOrWhiteSpace(media)) return null;

        var startNumber = int.TryParse(template.Attribute("startNumber")?.Value, out var start)
            ? start
            : 1;
        var timeline = template.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "SegmentTimeline");
        var times = new List<long>();
        if (timeline is not null)
        {
            var currentTime = 0L;
            foreach (var segment in timeline.Descendants()
                         .Where(element => element.Name.LocalName == "S"))
            {
                if (!long.TryParse(segment.Attribute("d")?.Value, out var duration) || duration <= 0)
                    continue;
                if (long.TryParse(segment.Attribute("t")?.Value, out var startTime))
                    currentTime = startTime;
                var repeat = int.TryParse(segment.Attribute("r")?.Value, out var r) ? r : 0;
                if (repeat < 0) repeat = 0; // open-ended repeat is not used by Tidal
                for (var i = 0; i <= repeat; i++)
                {
                    times.Add(currentTime);
                    currentTime += duration;
                }
            }
        }
        else
        {
            // Some DASH packs use a fixed segment duration and the MPD's
            // presentation duration instead of a SegmentTimeline.
            if (!long.TryParse(template.Attribute("duration")?.Value, out var duration)
                || duration <= 0)
                return null;
            var timescale = long.TryParse(template.Attribute("timescale")?.Value, out var scale)
                            && scale > 0
                ? scale
                : 1;
            var total = ScopeDuration(representation);
            if (total is null) return null;
            var segmentSeconds = (double)duration / timescale;
            var count = (long)Math.Ceiling(total.Value.TotalSeconds / segmentSeconds);
            if (count <= 0 || count > 100_000) return null;
            for (var i = 0L; i < count; i++) times.Add(i * duration);
        }
        if (times.Count == 0) return null;

        var segments = new List<string>(times.Count);
        var number = startNumber;
        foreach (var time in times)
        {
            segments.Add(new Uri(absolute,
                Substitute(media, representation, number, time)).ToString());
            number++;
        }

        return new DashPlan(
            new Uri(absolute, Substitute(init, representation, startNumber, 0)).ToString(),
            segments,
            codecs,
            representation.Parent?.Attribute("mimeType")?.Value ?? "audio/mp4",
            representation.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Label")?.Value);
    }

    /// <summary>
    /// A SegmentBase representation is one file: the initialization and the
    /// media live at byte ranges of the same URL. The index range, when
    /// present, separates the two; the media runs to EOF.
    /// </summary>
    private static DashPlan? BuildSegmentBasePlan(
        XElement representation, XElement segmentBase, Uri absolute, string codecs)
    {
        var baseUrl = NearestBaseUrl(representation) ?? NearestBaseUrl(representation.Parent);
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var mediaUrl = new Uri(absolute, baseUrl).ToString();

        var initialization = segmentBase.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Initialization");
        var initializationRange = ParseRange(initialization?.Attribute("range")?.Value);
        if (initialization is not null && initializationRange is null) return null;

        var indexRange = ParseRange(segmentBase.Attribute("indexRange")?.Value)
            ?? ParseRange(representation.Attribute("indexRange")?.Value);

        var mediaStart = 0L;
        if (initializationRange is not null)
            mediaStart = Math.Max(mediaStart,
                initializationRange.Start + (initializationRange.Length ?? 0));
        if (indexRange is not null)
            mediaStart = Math.Max(mediaStart, indexRange.Start + (indexRange.Length ?? 0));

        var initSource = initialization?.Attribute("sourceURL")?.Value;
        var initializationUrl = initialization is null
            ? ""
            : string.IsNullOrWhiteSpace(initSource)
                ? mediaUrl
                : new Uri(absolute, initSource).ToString();

        return new DashPlan(
            initializationUrl,
            new[] { mediaUrl },
            codecs,
            representation.Parent?.Attribute("mimeType")?.Value ?? "audio/mp4",
            representation.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Label")?.Value,
            initializationRange,
            mediaStart > 0 ? new ByteRange(mediaStart) : null);
    }

    /// <summary>The nearest BaseURL at or above the element, or null.</summary>
    private static string? NearestBaseUrl(XElement? element)
    {
        for (var node = element; node is not null; node = node.Parent)
        {
            var value = node.Elements()
                .FirstOrDefault(child => child.Name.LocalName == "BaseURL")?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    /// <summary>The period or presentation duration, in ISO 8601 form.</summary>
    private static TimeSpan? ScopeDuration(XElement representation)
    {
        for (var node = representation.Parent; node is not null; node = node.Parent)
        {
            var attribute = node.Name.LocalName == "MPD"
                ? "mediaPresentationDuration"
                : "duration";
            var value = node.Attribute(attribute)?.Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                return XmlConvert.ToTimeSpan(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Parses "start-end" or "start-" into a byte range.</summary>
    private static ByteRange? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('-', 2);
        if (!long.TryParse(parts[0], out var start) || start < 0) return null;
        if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1]))
            return new ByteRange(start);
        if (!long.TryParse(parts[1], out var end) || end < start) return null;
        return new ByteRange(start, end - start + 1);
    }

    private static DashPlan? BuildSegmentListPlan(
        XElement representation, XElement list, Uri absolute, string codecs)
    {
        var init = list.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Initialization")?
            .Attribute("sourceURL")?.Value;
        var media = list.Descendants()
            .Where(element => element.Name.LocalName == "SegmentURL")
            .Select(element => element.Attribute("media")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (string.IsNullOrWhiteSpace(init) || media.Count == 0) return null;

        var segments = media
            .Select((url, index) => new Uri(absolute,
                Substitute(url!, representation, index + 1, 0)).ToString())
            .ToList();
        return new DashPlan(
            new Uri(absolute, Substitute(init, representation, 1, 0)).ToString(),
            segments,
            codecs,
            representation.Parent?.Attribute("mimeType")?.Value ?? "audio/mp4",
            representation.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Label")?.Value);
    }

    private static string Substitute(
        string url, XElement representation, int number, long time)
        => url
            .Replace("$Number$", number.ToString(), StringComparison.Ordinal)
            .Replace("$Time$", time.ToString(), StringComparison.Ordinal)
            .Replace("$RepresentationID$", representation.Attribute("id")?.Value ?? "",
                StringComparison.Ordinal)
            .Replace("$Bandwidth$", representation.Attribute("bandwidth")?.Value ?? "",
                StringComparison.Ordinal);

    // ── Download ─────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads one track into <paramref name="outputDirectory"/>. The
    /// caller passes the Tidal quality tier. Flac output is remuxed with
    /// ffmpeg because Tidal serves FLAC inside fragmented MP4 segments;
    /// AAC is already a playable m4a once concatenated.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        long trackId, string quality, string outputDirectory, string? melodyId,
        CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(trackId, quality, ct);
        if (manifest.Status != ManifestStatus.Ok || manifest.ManifestUrl is null)
            throw new RhythmaxException(manifest.Message ?? "Rhythmax did not return a full manifest");

        var requested = quality == "LOSSLESS" ? "flac" : "aac";
        var plan = await ParseDashAsync(manifest.ManifestUrl, requested, ct)
            ?? throw new RhythmaxException("Rhythmax returned a manifest this plugin could not read");

        Directory.CreateDirectory(outputDirectory);
        var stem = melodyId is { Length: > 0 }
            ? melodyId
            : Guid.NewGuid().ToString("N")[..8];
        var work = Path.Combine(outputDirectory, $".mb-rhythmax-{stem}.mp4");
        var final = Path.Combine(outputDirectory, $"tidal-{trackId}-{stem}.{Extension(plan.Codecs)}");
        try
        {
            await using (var output = new FileStream(work, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (plan.InitializationUrl.Length > 0)
                    await CopyAsync(plan.InitializationUrl, output, plan.InitializationRange, ct);
                foreach (var segment in plan.SegmentUrls)
                    await CopyAsync(segment, output, plan.MediaRange, ct);
            }

            if (new FileInfo(work).Length == 0)
                throw new RhythmaxException("Rhythmax returned empty audio");

            if (IsFlac(plan.Codecs))
            {
                var ffmpeg = ArchiveCopier.FindFfmpeg();
                if (ffmpeg is null)
                    throw new RhythmaxException("ffmpeg is needed to turn Tidal's FLAC stream into a .flac file");
                var result = ToolProcessRunner.Run(ffmpeg, new[]
                {
                    "-y", "-v", "error", "-i", work, "-c:a", "copy", final,
                }, timeoutMs: 180000);
                if (result.TimedOut || result.ExitCode != 0)
                {
                    var reason = result.TimedOut ? "timed out" : result.Stderr.Trim();
                    throw new RhythmaxException(
                        "ffmpeg could not remux the Tidal FLAC" +
                        (string.IsNullOrWhiteSpace(reason) ? "" : ": " + Trim(reason, 200)));
                }
                FileCleanup.DeleteQuietly(work);
            }
            else
            {
                FileMove.MoveAcrossDevices(work, final);
            }

            var container = AudioSignature.EnsureExtension(final);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(final);
                throw new RhythmaxException("Rhythmax returned bytes that are not audio");
            }

            YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
            return new DownloadResult(container, plan.Codecs);
        }
        catch
        {
            FileCleanup.DeleteQuietly(work);
            FileCleanup.DeleteQuietly(final);
            throw;
        }
    }

    private async Task CopyAsync(
        string url, Stream output, ByteRange? range, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (range is not null)
            request.Headers.Range = range.Length is { } length
                ? new RangeHeaderValue(range.Start, range.Start + length - 1)
                : new RangeHeaderValue(range.Start, null);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (range is not null && response.StatusCode != HttpStatusCode.PartialContent)
            throw new RhythmaxException("Rhythmax ignored a byte range request");
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await source.CopyToAsync(output, 81920, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<string?> TokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is { Length: > 0 } && _tokenExpiresAt > DateTimeOffset.UtcNow)
            return _token;

        await _tokenGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is { Length: > 0 } && _tokenExpiresAt > DateTimeOffset.UtcNow)
                return _token;

            using var response = await _http.GetAsync(_baseUrl + "/token", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Rhythmax token endpoint answered {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;
            var token = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(token)) return null;

            var seconds = 600;
            if (root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
                && e.TryGetInt32(out var parsed))
                seconds = parsed;

            _token = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, seconds - 30));
            return _token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rhythmax token request failed");
            return null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 500 ? body[..500] : body;
        }
        catch
        {
            return "";
        }
    }

    private static string Codec(XElement representation)
        => representation.Attribute("codecs")?.Value ?? "";

    private static bool IsFlac(string codecs)
        => codecs.Contains("flac", StringComparison.OrdinalIgnoreCase);

    private static int CodecScore(string codecs, string requestedFormat)
    {
        if (requestedFormat == "flac")
            return IsFlac(codecs) ? 2 : 0;
        if (IsFlac(codecs)) return 0;
        if (codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) return 2;
        if (codecs.Contains("aac", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static bool CodecMatches(string codecs, string requestedFormat)
        => requestedFormat == "flac"
            ? IsFlac(codecs)
            : !IsFlac(codecs)
              && (codecs.Contains("mp4a", StringComparison.OrdinalIgnoreCase)
                  || codecs.Contains("aac", StringComparison.OrdinalIgnoreCase)
                  || codecs.Contains("ec-3", StringComparison.OrdinalIgnoreCase));

    private static string Extension(string codecs)
        => IsFlac(codecs) ? "flac" : "m4a";

    private static string Trim(string text, int max = 300)
        => text.Length <= max ? text : text[..max] + "...";
}

/// <summary>A Rhythmax failure with a message fit for the track row.</summary>
public sealed class RhythmaxException(string message) : Exception(message);
