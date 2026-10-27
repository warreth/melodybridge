using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Lucida;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches lucida.to's service chain (Qobuz NL, Amazon, Deezer FR, Tidal
/// US) for the Lucida downloader. The site sits behind Cloudflare, so the
/// session hands out cleared credentials and refreshes them on a challenge.
/// An ISRC is tried first; lucida's hits carry no ISRC, so a confident title
/// match is still required and the text chain follows when the code only
/// produces noise.
/// </summary>
public sealed class LucidaSearchProvider : ISearchProvider
{
    public const string ProviderId = "lucida-catalogue";
    public const string DownloaderId = "lucida";

    // Search services lucida.to supports, lossless-oriented and most-covered
    // first. Qobuz currently resolves through Lucida's Netherlands account,
    // Amazon selects an account itself, Deezer uses France and Tidal uses
    // the US storefront. The current community client uses exactly these
    // defaults; the old US Qobuz storefront returned no usable results.
    private static readonly (string Service, string Country)[] SearchServices =
    [
        ("qobuz", "NL"),
        ("amazon", ""),
        ("deezer", "FR"),
        ("tidal", "US"),
    ];

    private readonly LucidaSession _session;
    private readonly ILogger<LucidaSearchProvider> _logger;

    public LucidaSearchProvider(LucidaSession session, ILogger<LucidaSearchProvider> logger)
    {
        _session = session;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "Lucida service search";
    public string Description =>
        "Searches lucida.to's Qobuz, Amazon, Deezer and Tidal storefronts, ISRC first; a hit carries no code, so the title still has to match.";
    public SearchData Data => SearchData.Text | SearchData.Isrc;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Lucida service search, ISRC first") };

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        // One challenge solve up front; the solver caches the clearance for
        // every service request below, and SendWithRefreshAsync re-solves on
        // a 403. A throwing solver must not escape into the waterfall (the
        // manager has no per-plugin catch on the search path), so solve
        // inside try.
        CloudflareCredentials? credentials;
        try
        {
            credentials = await _session.SolveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Lucida solver failed for search: {Message}", ex.Message);
            _session.SetUnavailable("The FlareSolverr solver did not answer");
            return null;
        }
        if (credentials is null)
        {
            _session.SetUnavailable(
                "lucida.to answered a Cloudflare challenge the solver could not solve");
            return null;
        }

        // ISRC first when the playlist (or the resolver) knows one: Qobuz
        // and Amazon index ISRCs, so the code finds the exact recording
        // even when the title search is noisy. Services that treat it as
        // text return noise the ranker rejects, and the text chain below
        // takes over. Scoring always runs against the real artist/title.
        if (query.HasIsrc)
        {
            var byIsrc = await SearchServicesAsync(query, query.Isrc!, credentials, ct);
            if (byIsrc?.MatchConfidence == MatchConfidence.High) return byIsrc;
        }

        // Storefront indexes are literal: parenthetical mix suffixes and
        // featuring tags tank the hit rate, so every service gets the
        // cleaned terms (the raw pair stays for match scoring).
        var searchText = SearchTerms.Query(query.Artist, query.Title);
        if (searchText.Length == 0) return null;
        return await SearchServicesAsync(query, searchText, credentials, ct);
    }

    /// <summary>
    /// Runs the service chain with one query text: the first
    /// High-confidence hit wins, otherwise the best Low-confidence hit is
    /// kept so the caller can try the text query.
    /// </summary>
    private async Task<DownloaderSearchHit?> SearchServicesAsync(
        TrackQuery query, string searchText, CloudflareCredentials credentials, CancellationToken ct)
    {
        DownloaderSearchHit? best = null;
        foreach (var (service, country) in SearchServices)
        {
            var hit = await SearchServiceAsync(service, country, query, searchText, credentials, ct);
            if (hit is null) continue;

            // A High-confidence match cannot be beaten by a later service;
            // skip the remaining requests entirely.
            if (hit.MatchConfidence == MatchConfidence.High) return hit;
            best = hit;
        }
        return best;
    }

    /// <summary>
    /// Runs one search against a single lucida service. Any failure (network,
    /// missing blob, bad parse) returns null so the chain can try the next
    /// service instead of failing the whole search.
    /// </summary>
    private async Task<DownloaderSearchHit?> SearchServiceAsync(
        string service, string country, TrackQuery query, string searchText,
        CloudflareCredentials credentials, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{LucidaSession.BaseUrl}/search?query={Uri.EscapeDataString(searchText)}"
                + $"&service={service}"
                + (country.Length > 0 ? $"&country={country}" : string.Empty));
            request.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                request.Headers.Add("Cookie", credentials.CookieHeader);

            var reply = await _session.SendWithRefreshAsync(request, credentials, ct);
            if (!reply.Ok || reply.Body is null)
            {
                _logger.LogDebug("Lucida {Service} search answered HTTP {Status}", service, reply.Status);
                return null;
            }

            var blob = LucidaSession.Between(reply.Body, LucidaSession.PdStart, LucidaSession.PdEnd);
            if (blob is null) return null;

            return FindBestTrack(blob, query.Artist, query.Title, query.ExpectedDuration);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Lucida {Service} search failed: {Message}", service, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Picks the best matching track from the search page-data blob:
    /// every entry is a candidate, ranked by title/artist similarity plus
    /// duration proximity, with the runner-ups attached as alternatives.
    /// </summary>
    private static DownloaderSearchHit? FindBestTrack(
        string blob, string artist, string title, TimeSpan? expectedDuration)
    {
        try
        {
            using var doc = JsonDocument.Parse(blob, LucidaSession.JsonOptions);
            var node = FindTracksNode(doc.RootElement);
            var tracks = node?.GetPropertySafe("tracks");
            if (tracks is not { ValueKind: JsonValueKind.Array }
                || tracks.Value.GetArrayLength() == 0)
                return null;

            var candidates = new List<DownloaderSearchHit>();
            foreach (var entry in tracks.Value.EnumerateArray())
            {
                var url = entry.GetPropertySafe("url")?.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                var entryTitle = entry.GetPropertySafe("title")?.GetString() ?? "";
                var entryArtist = ReadArtists(entry);
                var durationMs = entry.GetPropertySafe("duration")?.GetInt64Safe();

                candidates.Add(new DownloaderSearchHit(
                    entryTitle,
                    entryArtist,
                    url,
                    durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, title, entryArtist, entryTitle)));
            }

            return SearchRanker.Rank(artist, title, expectedDuration, candidates);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadArtists(JsonElement entry)
    {
        var artists = entry.GetPropertySafe("artists");
        if (artists is { ValueKind: JsonValueKind.Array })
        {
            return string.Join(", ",
                artists.Value.EnumerateArray()
                    .Select(a => a.GetPropertySafe("name")?.GetString() ?? "")
                    .Where(name => name.Length > 0));
        }

        // Some SvelteKit releases flatten a single artist to text.
        return artists?.GetString() ?? entry.GetPropertySafe("artist")?.GetString() ?? "";
    }

    private static JsonElement? FindTracksNode(JsonElement element, int depth = 0)
    {
        if (depth > 8) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var tracks = element.GetPropertySafe("tracks");
                if (tracks is { ValueKind: JsonValueKind.Array })
                    return element;
                foreach (var value in element.EnumerateObject())
                {
                    var found = FindTracksNode(value.Value, depth + 1);
                    if (found is not null) return found;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray())
                {
                    var found = FindTracksNode(value, depth + 1);
                    if (found is not null) return found;
                }
                return null;
            default:
                return null;
        }
    }
}
