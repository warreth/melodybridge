using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.FlacMusic;

/// <summary>
/// The last-resort source: flac.music.hi.cn, a Chinese front-end for Kuwo
/// and NetEase. It has no ISRC index and no stable track URL, so it is the
/// one built-in source that still searches itself. The plugin is appended to
/// the end of the shipped order and is only reached after the ISRC-capable
/// sources and the ordinary fallbacks had their turn.
///
/// It offers exactly the format set the site exposes: FLAC (16-bit/44.1 kHz
/// in practice), MP3 320 and MP3 128.
/// </summary>
public class FlacMusicDownloader : IDownloader, IPluginAvailabilityRefresh
{
    public const string DefaultBaseUrl = FlacMusicClient.DefaultBaseUrl;
    private const string BaseUrlKey = "base_url";
    private const string PlatformKey = "platform";
    private const string PlatformBoth = "both";

    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
    ]);

    private static readonly Regex ChineseTitleSuffix = new(
        @"\s*[-–—]\s*《.*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Id => "flacmusic";
    public string Name => "FLAC Music (CN)";
    public string Description =>
        "Kuwo and NetEase lossless search, used last because it has no ISRC index";

    public PluginCapabilities Capabilities => Caps;

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.Self(SearchData.Text,
            "Title-only fallback; no ISRC support");

    /// <summary>
    /// It is the fallback, full stop. Even a lossless request keeps every
    /// curated source ahead of it: the site has no ISRC index and its
    /// quality labels do not always match the bytes.
    /// </summary>
    public bool FallbackOnly => true;

    public string? LastUnavailableReason { get; private set; }

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(PlatformKey, "Search platform", null,
            "Both covers Kuwo and NetEase; pick one when a track only exists there.",
            Options:
            [
                new(PlatformBoth, "Both (Kuwo + NetEase)"),
                new("kuwo", "Kuwo"),
                new("wyy", "NetEase Cloud Music"),
            ]),
        new(BaseUrlKey, "Site base URL", DefaultBaseUrl,
            "Only for a mirror. Empty uses flac.music.hi.cn.",
            Advanced: true),
    ];

    private readonly FlacMusicClient _client;
    private readonly IPluginConfig _config;
    private readonly ILogger<FlacMusicDownloader> _logger;
    private readonly AvailabilityCache _availability = new();

    public FlacMusicDownloader(
        FlacMusicClient client, IPluginConfig config, ILogger<FlacMusicDownloader> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public void RefreshAvailability() => _availability.Invalidate();

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached)) return cached;

        _client.BaseUrl = await BaseUrlAsync(ct);
        var (ready, reason) = await _client.CheckHealthAsync(ct);
        LastUnavailableReason = ready ? null : reason;
        _availability.Store(ready);
        return ready;
    }

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var candidates = await BuildCandidatesAsync(query, ct);
        if (candidates.Count == 0) return null;
        return SearchRanker.Rank(
            query.Artist, query.Title, query.ExpectedDuration, candidates,
            maxAlternatives: 3,
            tieBreaker: QualityRank);
    }

    /// <summary>All ranked candidates, for the manual source picker.</summary>
    public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        TrackQuery query, CancellationToken ct = default)
        => (await BuildCandidatesAsync(query, ct))
            .OrderByDescending(c => FuzzyMatcher.ScoreWithDuration(
                query.Artist, query.Title, query.ExpectedDuration, c.Artist, c.Title, c.Duration))
            .ThenByDescending(QualityRank)
            .Take(10)
            .ToList();

    private async Task<List<DownloaderSearchHit>> BuildCandidatesAsync(
        TrackQuery query, CancellationToken ct)
    {
        var keyword = query.SearchText;
        if (keyword.Length == 0) return new List<DownloaderSearchHit>();

        _client.BaseUrl = await BaseUrlAsync(ct);
        var platforms = await PlatformsAsync(ct);
        var results = await Task.WhenAll(platforms.Select(platform =>
            _client.SearchAsync(platform, keyword, size: 20, ct)));

        var failed = results.Where(r => r.Status != FlacMusicClient.Status.Ok).ToList();
        if (failed.Count == results.Length)
        {
            var reason = failed.Select(f => f.Message).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
            LastUnavailableReason = reason ?? "flac.music.hi.cn could not be searched";
            _logger.LogDebug("FLAC Music search failed: {Reason}", LastUnavailableReason);
            return new List<DownloaderSearchHit>();
        }

        var hits = new List<DownloaderSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in results.SelectMany(r => r.Tracks))
        {
            if (!seen.Add($"{track.Platform}:{track.Id}")) continue;
            var format = SelectFormat(track.Formats, query.Quality);
            if (format is null) continue;

            var title = CleanTitle(track.Title);
            var hit = new DownloaderSearchHit(
                Title: title,
                Artist: track.Artist,
                SourceUrl: EncodeSourceUrl(track, title, query.Quality),
                Duration: track.Duration,
                BitrateKbps: format.IsFlac ? null : format.BitrateKbps,
                MatchConfidence: FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: title));
            hits.Add(hit);
        }
        return hits;
    }

    /// <summary>
    /// The format that fits the requested quality. FLAC wins for lossless
    /// and unrestricted requests; a bitrate cap walks down to the highest
    /// MP3 that fits. AAC and Opus are not offered by the site, so those
    /// requests never route here.
    /// Internal for tests.
    /// </summary>
    internal static FlacMusicClient.Format? SelectFormat(
        IReadOnlyList<FlacMusicClient.Format> formats, DownloadQuality quality)
    {
        var flac = formats
            .Where(f => f.IsFlac)
            .OrderByDescending(f => f.BitrateKbps)
            .FirstOrDefault();
        var mp3 = formats
            .Where(f => !f.IsFlac)
            .OrderByDescending(f => f.BitrateKbps)
            .ToList();

        if (quality.Format == AudioFormat.Flac) return flac;
        if (quality.Format is AudioFormat.Aac or AudioFormat.Opus) return null;
        if (quality.Format == AudioFormat.Mp3)
            return mp3.FirstOrDefault(f => quality.IsWithinBand(f.BitrateKbps));

        // Auto: real lossless when no cap rules it out, otherwise the best
        // lossy file inside the band.
        var losslessAllowed = quality.MaxKbps is null or >= 900
            || quality.MinKbps is >= 900;
        if (losslessAllowed && flac is not null) return flac;
        return mp3.FirstOrDefault(f => quality.IsWithinBand(f.BitrateKbps));
    }

    private static int QualityRank(DownloaderSearchHit hit)
        => hit.BitrateKbps ?? 10000;

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (!TryParseSourceUrl(sourceUrl, out var link))
            return new(false, null, "not a FLAC Music track URL: " + sourceUrl);

        _client.BaseUrl = await BaseUrlAsync(ct);
        var requested = quality ?? DownloadQuality.Any;
        var track = await ResolveTrackAsync(link, ct);
        if (track is null)
            return new(false, null, "flac.music.hi.cn no longer lists this track");

        var format = SelectFormat(track.Formats, requested);
        if (format is null)
            return new(false, null,
                $"FLAC Music cannot serve {Describe(requested)} for this track");

        var url = await _client.GetUrlAsync(track, format, ct);
        if (url.Status == FlacMusicClient.Status.Expired)
        {
            // The encoded sign belongs to the search result and expires
            // quickly. Refreshing by id returns a fresh one without a text
            // search, so a retry still names the same recording.
            var refreshed = await _client.GetTrackAsync(link.Platform, link.Id, ct);
            track = refreshed.Tracks.FirstOrDefault(t => t.Id == link.Id)
                ?? refreshed.Tracks.FirstOrDefault();
            if (track is null)
                return new(false, null, "flac.music.hi.cn no longer lists this track");
            format = SelectFormat(track.Formats, requested);
            if (format is null)
                return new(false, null,
                    $"FLAC Music cannot serve {Describe(requested)} for this track");
            url = await _client.GetUrlAsync(track, format, ct);
        }
        if (url.Status != FlacMusicClient.Status.Ok || url.Url is null)
            return new(false, null, url.Message ?? "flac.music.hi.cn did not return a download URL");

        // The site labels a tier by its own rules, not by the bytes at the
        // CDN: some Kuwo "128 mp3" rows answer with an AAC/100 stream. An
        // automatic quality accepts either, but an explicit container
        // request must not be handed the wrong one silently. Returning the
        // mismatch lets the waterfall try the next candidate (the other
        // platform often serves the real MP3).
        if (requested.Format == AudioFormat.Mp3
            && url.Format is { Length: > 0 } actualMp3
            && !actualMp3.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            return new(false, null,
                $"flac.music.hi.cn served {actualMp3} for the requested mp3 tier");
        if (requested.Format == AudioFormat.Flac
            && url.Format is { Length: > 0 } actualFlac
            && !actualFlac.Equals("flac", StringComparison.OrdinalIgnoreCase))
            return new(false, null,
                $"flac.music.hi.cn served {actualFlac} for the requested flac tier");

        Directory.CreateDirectory(outputDirectory);
        var stem = melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8];
        var temp = Path.Combine(outputDirectory, $".mb-flacmusic-{stem}.part");
        try
        {
            var download = await _client.DownloadFileAsync(url.Url, temp, ct);
            if (!download.Ok)
                return new(false, null, download.Error ?? "the audio CDN did not deliver the file");
            if (new FileInfo(temp).Length == 0)
                return new(false, null, "the audio CDN returned an empty file");

            var sniffed = AudioSignature.EnsureExtension(temp);
            if (sniffed is null)
                return new(false, null, "the audio CDN returned bytes that are not audio");

            var target = FileMove.UniqueTarget(outputDirectory,
                $"flacmusic-{link.Platform}-{link.Id}-{stem}{Path.GetExtension(sniffed)}");
            FileMove.MoveAcrossDevices(sniffed, target);
            YtDlpDownloader.TagDownloadedFile(target, expectedTitle: track.Title);
            _availability.Store(true);
            LastUnavailableReason = null;
            return new(true, target, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FileCleanup.DeleteQuietly(temp);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FLAC Music download {Platform}/{Id} failed",
                link.Platform, link.Id);
            return new(false, null, ex.Message);
        }
        finally
        {
            FileCleanup.DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// Reads the fresh track row by id. The encoded URL carries the search
    /// result's sign and metadata, but the site's song endpoint is the
    /// authoritative (and unexpired) copy.
    /// </summary>
    private async Task<FlacMusicClient.Track?> ResolveTrackAsync(
        SourceLink link, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(link.Time) && !string.IsNullOrWhiteSpace(link.Sign)
            && link.Formats.Count > 0)
        {
            // The encoded URL is enough for the first attempt; the site is
            // only asked again when getUrl says the sign expired.
            return new FlacMusicClient.Track(
                link.Platform, link.Id, link.Title ?? link.Id,
                link.Artist ?? "", link.Album, link.Duration,
                link.Formats, link.Time, link.Sign, null, null);
        }

        var result = await _client.GetTrackAsync(link.Platform, link.Id, ct);
        return result.Tracks.FirstOrDefault(t => t.Id == link.Id)
            ?? result.Tracks.FirstOrDefault();
    }

    /// <summary>The encoded source URL and the direct Kuwo/NetEase link shape. Internal for tests.</summary>
    internal static bool TryParseSourceUrl(string sourceUrl, out SourceLink link)
    {
        link = new SourceLink();
        if (string.IsNullOrWhiteSpace(sourceUrl) || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            return false;

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/');
        var trackIndex = Array.FindIndex(path, p => p.Equals("track", StringComparison.OrdinalIgnoreCase));
        if (trackIndex >= 0 && trackIndex + 2 < path.Length)
        {
            link = new SourceLink
            {
                Platform = path[trackIndex + 1],
                Id = path[trackIndex + 2],
            };
        }
        else if (uri.Host.Contains("kuwo.cn", StringComparison.OrdinalIgnoreCase))
        {
            var id = path.LastOrDefault(p => p.Length > 0 && p.All(char.IsAsciiDigit));
            if (id is null) return false;
            link = new SourceLink { Platform = "kuwo", Id = id };
        }
        else if (uri.Host.Contains("music.163.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Contains("163cn.tv", StringComparison.OrdinalIgnoreCase))
        {
            var query = ParseQuery(uri.Query);
            if (!query.TryGetValue("id", out var id))
            {
                var fragment = uri.Fragment.TrimStart('#');
                var question = fragment.IndexOf('?');
                if (question >= 0)
                    ParseQuery(fragment[question..])
                        .ToList()
                        .ForEach(kv => query[kv.Key] = kv.Value);
                query.TryGetValue("id", out id);
            }
            if (string.IsNullOrWhiteSpace(id)) return false;
            link = new SourceLink { Platform = "wyy", Id = id };
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(link.Platform) || string.IsNullOrWhiteSpace(link.Id))
            return false;

        var parameters = ParseQuery(uri.Query);
        link.Time = parameters.GetValueOrDefault("time");
        link.Sign = parameters.GetValueOrDefault("sign");
        link.Artist = parameters.GetValueOrDefault("artist");
        link.Title = parameters.GetValueOrDefault("title");
        link.Album = parameters.GetValueOrDefault("album");
        if (int.TryParse(parameters.GetValueOrDefault("duration"), out var seconds))
            link.Duration = TimeSpan.FromSeconds(seconds);
        if (parameters.GetValueOrDefault("formats") is { Length: > 0 } formats)
            link.Formats = formats.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(':', StringSplitOptions.RemoveEmptyEntries))
                .Where(pair => pair.Length == 2
                               && int.TryParse(pair[1], out _))
                .Select(pair => new FlacMusicClient.Format(
                    pair[0].ToLowerInvariant(), int.Parse(pair[1])))
                .ToList();
        return true;
    }

    /// <summary>The encoded URL the search hit carries. Internal for tests.</summary>
    internal static string EncodeSourceUrl(
        FlacMusicClient.Track track, string title, DownloadQuality quality)
    {
        var formats = string.Join(",",
            track.Formats.Select(f => $"{f.Name}:{f.BitrateKbps}"));
        var query = new List<string>
        {
            "formats=" + Uri.EscapeDataString(formats),
            "artist=" + Uri.EscapeDataString(track.Artist ?? ""),
            "title=" + Uri.EscapeDataString(title),
        };
        if (!string.IsNullOrWhiteSpace(track.Album))
            query.Add("album=" + Uri.EscapeDataString(track.Album));
        if (track.Duration is { } duration)
            query.Add("duration=" + ((int)Math.Round(duration.TotalSeconds)).ToString());
        if (!string.IsNullOrWhiteSpace(track.Time))
            query.Add("time=" + Uri.EscapeDataString(track.Time));
        if (!string.IsNullOrWhiteSpace(track.Sign))
            query.Add("sign=" + Uri.EscapeDataString(track.Sign));
        return $"{DefaultBaseUrl}/track/{Uri.EscapeDataString(track.Platform)}/"
            + $"{Uri.EscapeDataString(track.Id)}?{string.Join("&", query)}";
    }

    /// <summary>"Shoot to Thrill-《Iron Man 2》" -> "Shoot to Thrill". Internal for tests.</summary>
    internal static string CleanTitle(string title)
    {
        var cleaned = ChineseTitleSuffix.Replace(title, "").Trim();
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    public Task<CandidateAudioQuality?> ProbeQualityAsync(
        string sourceUrl, CancellationToken ct = default)
    {
        if (!TryParseSourceUrl(sourceUrl, out var link)) return Task.FromResult<CandidateAudioQuality?>(null);
        var flac = link.Formats.FirstOrDefault(f => f.IsFlac);
        if (flac is not null)
            return Task.FromResult<CandidateAudioQuality?>(
                new("flac", null, 44100, 16, true));
        var mp3 = link.Formats.Where(f => !f.IsFlac)
            .OrderByDescending(f => f.BitrateKbps)
            .FirstOrDefault();
        return Task.FromResult(mp3 is null
            ? null
            : new CandidateAudioQuality("mp3", mp3.BitrateKbps, null, null, false));
    }

    private async Task<string> BaseUrlAsync(CancellationToken ct)
    {
        var value = await _config.GetAsync(Id, BaseUrlKey, ct);
        return string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim();
    }

    private async Task<IReadOnlyList<string>> PlatformsAsync(CancellationToken ct)
    {
        var value = await _config.GetAsync(Id, PlatformKey, ct);
        return string.IsNullOrWhiteSpace(value) || value.Equals(PlatformBoth, StringComparison.OrdinalIgnoreCase)
            ? new[] { "kuwo", "wyy" }
            : new[] { value.Trim().ToLowerInvariant() };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?', '#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? "" : pair[(separator + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }
        return result;
    }

    private static string Describe(DownloadQuality quality)
    {
        var format = quality.Format == AudioFormat.Auto ? "any format" : quality.Format.ToString();
        return (quality.MinKbps, quality.MaxKbps) switch
        {
            (null, null) => format,
            (null, { } max) => $"{format} up to {max} kbps",
            ({ } min, null) => $"{format} at least {min} kbps",
            ({ } min, { } max) => $"{format} {min}-{max} kbps",
        };
    }

    /// <summary>One parsed source URL.</summary>
    internal sealed record SourceLink
    {
        public string Platform { get; init; } = "";
        public string Id { get; init; } = "";
        public string? Time { get; set; }
        public string? Sign { get; set; }
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public string? Album { get; set; }
        public TimeSpan? Duration { get; set; }
        public IReadOnlyList<FlacMusicClient.Format> Formats { get; set; } =
            Array.Empty<FlacMusicClient.Format>();
    }
}
