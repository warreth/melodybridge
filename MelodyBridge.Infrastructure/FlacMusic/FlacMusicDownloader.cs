using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Infrastructure.FlacMusic;

/// <summary>
/// The last-resort source: flac.music.hi.cn, a Chinese front-end for Kuwo
/// and NetEase. It has no ISRC index or stable track URL, so
/// FlacMusicSearchProvider searches its catalogue and this downloader only
/// resolves the URL it gets back. The plugin is appended to the end of the
/// shipped order and is only reached after the ISRC-capable sources and the
/// ordinary fallbacks had their turn.
///
/// It offers exactly the format set the site exposes: FLAC (16-bit/44.1 kHz
/// in practice), MP3 320 and MP3 128.
/// </summary>
public class FlacMusicDownloader : IDownloader, IPluginAvailabilityRefresh
{
    public const string DefaultBaseUrl = FlacMusicSession.DefaultBaseUrl;
    internal const string BaseUrlKey = FlacMusicSession.BaseUrlKey;
    internal const string PlatformKey = FlacMusicSession.PlatformKey;
    internal const string PlatformBoth = FlacMusicSession.PlatformBoth;

    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
    ]);

    public string Id => "flacmusic";
    public string Name => "FLAC Music (CN)";
    public string Description =>
        "Kuwo and NetEase lossless search, used last because it has no ISRC index";

    public PluginCapabilities Capabilities => Caps;

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Text,
            "Title-only Kuwo and NetEase search; no ISRC support");

    public string IconName => "globe";
    public string? GuideUrl => "https://docs.melodybridge.app/flacmusic";
    public IReadOnlyList<string> Notes => new[] { "title-only fallback", "Kuwo and NetEase" };

    /// <summary>
    /// It is the fallback, full stop. Even a lossless request keeps every
    /// curated source ahead of it: the site has no ISRC index and its
    /// quality labels do not always match the bytes.
    /// </summary>
    public bool FallbackOnly => true;

    public string? LastUnavailableReason => _session.LastUnavailableReason;

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

    private readonly FlacMusicSession _session;
    private readonly ILogger<FlacMusicDownloader> _logger;

    public FlacMusicDownloader(
        FlacMusicSession session, ILogger<FlacMusicDownloader> logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <summary>Test and one-off host convenience: creates the session from the same inputs.</summary>
    public FlacMusicDownloader(
        FlacMusicClient client, IPluginConfig config, ILogger<FlacMusicDownloader> logger)
        : this(new FlacMusicSession(client, config,
            NullLogger<FlacMusicSession>.Instance), logger)
    {
    }

    public void RefreshAvailability() => _session.RefreshAvailability();

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _session.IsAvailableAsync(ct);

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

        _session.Client.BaseUrl = await _session.BaseUrlAsync(ct);
        var requested = quality ?? DownloadQuality.Any;
        var track = await ResolveTrackAsync(link, ct);
        if (track is null)
            return new(false, null, "flac.music.hi.cn no longer lists this track");

        var format = FlacMusicSource.SelectFormat(track.Formats, requested);
        if (format is null)
            return new(false, null,
                $"FLAC Music cannot serve {Describe(requested)} for this track");

        var url = await _session.Client.GetUrlAsync(track, format, ct);
        if (url.Status == FlacMusicClient.Status.Expired)
        {
            // The encoded sign belongs to the search result and expires
            // quickly. Refreshing by id returns a fresh one without a text
            // search, so a retry still names the same recording.
            var refreshed = await _session.Client.GetTrackAsync(link.Platform, link.Id, ct);
            track = refreshed.Tracks.FirstOrDefault(t => t.Id == link.Id)
                ?? refreshed.Tracks.FirstOrDefault();
            if (track is null)
                return new(false, null, "flac.music.hi.cn no longer lists this track");
            format = FlacMusicSource.SelectFormat(track.Formats, requested);
            if (format is null)
                return new(false, null,
                    $"FLAC Music cannot serve {Describe(requested)} for this track");
            url = await _session.Client.GetUrlAsync(track, format, ct);
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
            var download = await _session.Client.DownloadFileAsync(url.Url, temp, ct);
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
            _session.RefreshAvailability();
            _session.SetUnavailable(null);
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

        var result = await _session.Client.GetTrackAsync(link.Platform, link.Id, ct);
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
        else if (HostIs(uri, "kuwo.cn"))
        {
            var id = path.LastOrDefault(p => p.Length > 0 && p.All(char.IsAsciiDigit));
            if (id is null) return false;
            link = new SourceLink { Platform = "kuwo", Id = id };
        }
        else if (HostIs(uri, "music.163.com") || HostIs(uri, "163cn.tv"))
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

    /// <summary>Exact host or a real subdomain, never a lookalike.</summary>
    private static bool HostIs(Uri uri, string domain)
        => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

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
