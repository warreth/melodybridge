using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches archive.org's public metadata for an audio item and ranks the
/// MP3 files inside it. The archive indexes metadata text, not ISRCs, so
/// this is a self-search provider: its tier stays last after the official
/// and community catalogues.
/// </summary>
public sealed class ArchiveOrgSearchProvider : ISearchProvider
{
    public const string ProviderId = "archiveorg-search";
    public const string DownloaderId = "archiveorg";

    private readonly HttpClient _http;
    private readonly ILogger<ArchiveOrgSearchProvider> _logger;

    public ArchiveOrgSearchProvider(
        HttpClient http, ILogger<ArchiveOrgSearchProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "Internet Archive search";
    public string Description =>
        "Searches archive.org's public metadata and ranks the MP3 files inside the matching items.";
    public SearchData Data => SearchData.Text;
    public SearchTier Tier => SearchTier.SelfSearch;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Internet Archive title search",
            Tier: SearchTier.SelfSearch) };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        var candidates = await BuildCandidatesAsync(query, ct);
        if (candidates.Count == 0) return null;
        var fitting = candidates
            .Where(candidate => query.Quality.IsWithinBand(candidate.BitrateKbps))
            .ToList();
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, fitting);
    }

    private async Task<List<DownloaderSearchHit>> BuildCandidatesAsync(
        TrackQuery query, CancellationToken ct)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return new List<DownloaderSearchHit>();

        try
        {
            // Only items that actually contain MP3 files. The archive
            // indexes metadata text, not ISRCs, so the ISRC is unused.
            var q = Uri.EscapeDataString($"{searchText} AND mediatype:audio AND format:(VBR MP3)");
            var url = $"{ArchiveOrgApi.SearchBase}?q={q}&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&rows=5&page=1&output=json";

            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("docs", out var docs) ||
                docs.ValueKind != JsonValueKind.Array || docs.GetArrayLength() == 0)
                return new List<DownloaderSearchHit>();

            // Collect candidates from every returned item (an item usually
            // holds a whole album): the right track may be the fifth item
            // or the eighth file inside one, so nothing is trusted by
            // position. The query already asks for five rows.
            var candidates = new List<DownloaderSearchHit>();
            foreach (var docItem in docs.EnumerateArray())
            {
                var identifier = GetString(docItem, "identifier");
                if (string.IsNullOrEmpty(identifier)) continue;

                candidates.AddRange(await FindMp3CandidatesAsync(
                    identifier!, query.Artist, query.Title, ct));
            }
            return candidates;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Archive.org search failed for '{Query}'", searchText);
        }

        return new List<DownloaderSearchHit>();
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
            using var resp = await _http.GetAsync(ArchiveOrgApi.MetadataUrl(identifier), ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
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
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, fallbackTitle, fileArtist, fileTitle)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
