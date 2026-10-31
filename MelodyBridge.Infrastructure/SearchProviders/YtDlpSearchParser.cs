using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Parses yt-dlp's flat playlist JSON into ranked candidates. Shared by
/// the YouTube and SoundCloud search providers; the downloaders only
/// receive the URL afterwards.
/// </summary>
internal static class YtDlpSearchParser
{
    public static List<DownloaderSearchHit> ParseFlatPlaylistHits(
        string stdout, string artist, string fallbackTitle, Func<string, string> urlFromId)
    {
        var hits = new List<DownloaderSearchHit>();
        if (string.IsNullOrWhiteSpace(stdout)) return hits;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var entries = root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().ToList()
                : new List<JsonElement> { root };
            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var hit = ParseEntry(entry, artist, fallbackTitle, urlFromId);
                if (hit is not null) hits.Add(hit);
            }
        }
        catch (JsonException)
        {
            // Unparseable yt-dlp output is a plugin miss, not a crash.
        }
        return hits;
    }

    private static DownloaderSearchHit? ParseEntry(
        JsonElement entry, string artist, string fallbackTitle, Func<string, string> urlFromId)
    {
        var id = entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
        var sourceUrl = ExtractSourceUrl(entry, id, urlFromId);
        if (string.IsNullOrEmpty(sourceUrl)) return null;

        var hitTitle = (entry.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null) ?? fallbackTitle;

        // The real artist: flat entries carry the channel/uploader; the old
        // code compared the requested artist against the hit TITLE, so
        // "Regard" never matched "Regard - Ride It (Official Video)".
        var hitArtist = entry.TryGetProperty("uploader", out var up) && up.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(up.GetString()) ? up.GetString()
            : entry.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ch.GetString()) ? ch.GetString()
                : SplitArtistFromTitle(hitTitle) ?? artist;
        // Channel decorations are not part of the name.
        hitArtist = StripChannelDecorations(hitArtist);

        return new DownloaderSearchHit(
            Title: hitTitle,
            Artist: hitArtist,
            SourceUrl: sourceUrl,
            Duration: entry.TryGetProperty("duration", out var d) && d.TryGetDouble(out var secs)
                ? TimeSpan.FromSeconds(secs)
                : null,
            MatchConfidence: FuzzyMatcher.Confidence(
                artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
    }

    /// <summary>
    /// The entry's URL: a full webpage URL when the extractor provides
    /// one, otherwise built from the bare id. A bare non-URL "url" field
    /// (some extractors put just the id there) is never passed through.
    /// </summary>
    private static string? ExtractSourceUrl(JsonElement entry, string? id, Func<string, string> urlFromId)
    {
        foreach (var property in new[] { "webpage_url", "url" })
        {
            if (!entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                continue;
            var text = value.GetString();
            if (!string.IsNullOrEmpty(text) && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return text;
        }
        return string.IsNullOrEmpty(id) ? null : urlFromId(id);
    }

    /// <summary>"Artist - Title" uploader strings: splits off the artist.</summary>
    private static string? SplitArtistFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var parts = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Length > 0 ? parts[0] : null;
    }

    /// <summary>
    /// Drops the suffixes YouTube glues onto channel names ("RegardVEVO",
    /// "Regard - Topic", "Regard Official") so the artist inside still
    /// matches what was requested.
    /// </summary>
    private static string StripChannelDecorations(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return artist ?? "";
        var trimmed = artist.Trim();
        foreach (var suffix in new[] { " - Topic", "VEVO", " Official", "Official" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed[..^suffix.Length].Trim();
                if (stripped.Length > 0) trimmed = stripped;
            }
        }
        return trimmed;
    }
}
