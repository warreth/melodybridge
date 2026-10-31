using System.Text.Json;
using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// Searches the Soulseek network through the user's own slskd instance.
/// The provider owns the search polling and the response parser; the
/// downloader only downloads the transfer reference the hit carries.
/// </summary>
public sealed class SlskdSearchProvider : ISearchProvider
{
    public const string ProviderId = "slskd-search";
    public const string DownloaderId = "slskd";

    private static readonly Regex TrackNumberPrefix = new(
        @"^\d{1,3}[\s._-]+", RegexOptions.Compiled);

    private readonly SlskdSession _session;
    private readonly ILogger<SlskdSearchProvider> _logger;

    public SlskdSearchProvider(SlskdSession session, ILogger<SlskdSearchProvider> logger)
    {
        _session = session;
        _logger = logger;
    }

    public string Id => ProviderId;
    public string Name => "Soulseek search";
    public string Description => "Searches the Soulseek network through your own slskd instance.";
    public SearchData Data => SearchData.Text;
    public SearchTier Tier => SearchTier.SelfSearch;
    public IReadOnlyList<SearchProviderTarget> Targets =>
        new[] { new SearchProviderTarget(DownloaderId, Data, "Soulseek peer search via slskd",
            Tier: SearchTier.SelfSearch) };

    /// <summary>How long a Soulseek search may take before giving up.</summary>
    internal TimeSpan SearchPollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal TimeSpan SearchPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public async Task<DownloaderSearchHit?> SearchAsync(
        string downloaderId, TrackQuery query, CancellationToken ct = default)
    {
        if (!string.Equals(downloaderId, DownloaderId, StringComparison.OrdinalIgnoreCase))
            return null;

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;
        if ((await _session.ApiKeyAsync(ct)).Length == 0) return null;

        string? searchId = null;
        try
        {
            using (var created = await _session.SendJsonAsync(
                       HttpMethod.Post, "/api/v0/searches", new { searchText }, ct))
            {
                if (created is null) return null;
                searchId = SlskdJson.GetString(created.RootElement, "id");
                if (string.IsNullOrEmpty(searchId)) return null;
            }

            if (!await WaitForSearchAsync(searchId, ct)) return null;

            using var responses = await _session.SendJsonAsync(
                HttpMethod.Get, $"/api/v0/searches/{searchId}/responses", null, ct);
            if (responses is null) return null;
            return ParseResponses(responses.RootElement, query);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("slskd search timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "slskd search '{Query}' failed", searchText);
            return null;
        }
        finally
        {
            await CleanupSearchAsync(searchId);
        }
    }

    private async Task<bool> WaitForSearchAsync(string searchId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + SearchPollTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using (var status = await _session.SendJsonAsync(
                       HttpMethod.Get, $"/api/v0/searches/{searchId}", null, ct))
            {
                if (status is not null)
                {
                    var state = SlskdJson.GetString(status.RootElement, "state") ?? "";
                    if (SearchIsComplete(state)) return true;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogDebug("slskd search did not complete within {Seconds:0} seconds",
                    SearchPollTimeout.TotalSeconds);
                return false;
            }
            await Task.Delay(SearchPollInterval, ct);
        }
    }

    /// <summary>True when the search will not produce more responses.</summary>
    internal static bool SearchIsComplete(string state)
        => state.Contains("Completed", StringComparison.OrdinalIgnoreCase)
        || state.Contains("ResponseLimitReached", StringComparison.OrdinalIgnoreCase)
        || state.Contains("FileLimitReached", StringComparison.OrdinalIgnoreCase);

    private async Task CleanupSearchAsync(string? searchId)
    {
        if (searchId is null) return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await _session.SendJsonAsync(
                HttpMethod.Delete, $"/api/v0/searches/{searchId}", null, cleanup.Token);
        }
        catch { /* cleanup is best effort */ }
    }

    /// <summary>
    /// Turns the responses into ranked candidates: locked files and
    /// containers the plugin does not declare are dropped, files outside
    /// the requested band are dropped, and the ranker picks the best
    /// match with free upload slots and lossless files as tie-breakers.
    /// </summary>
    internal static DownloaderSearchHit? ParseResponses(JsonElement root, TrackQuery query)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;

        var candidates = new List<DownloaderSearchHit>();
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var response in root.EnumerateArray())
        {
            if (response.ValueKind != JsonValueKind.Object) continue;
            var username = SlskdJson.GetString(response, "username");
            if (string.IsNullOrEmpty(username)) continue;

            var freeSlot = response.TryGetProperty("hasFreeUploadSlot", out var slot)
                           && slot.ValueKind == JsonValueKind.True;

            if (!response.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                if (file.TryGetProperty("isLocked", out var locked) &&
                    locked.ValueKind == JsonValueKind.True)
                    continue;

                var filename = SlskdJson.GetString(file, "filename");
                if (string.IsNullOrEmpty(filename)) continue;

                var container = ContainerFor(filename);
                if (container is null) continue;

                var size = file.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt64()
                    : 0;
                if (size <= 0) continue;

                int? kbps = file.TryGetProperty("bitRate", out var b) && b.ValueKind == JsonValueKind.Number
                    ? b.GetInt32()
                    : null;
                // Lossless files report no bitrate: check the band against
                // a typical lossless size, so an "up to 320" request never
                // settles for a FLAC the measurement would reject.
                var effectiveKbps = container == AudioFormat.Flac ? 900 : kbps;
                if (!query.Quality.IsWithinBand(effectiveKbps)) continue;

                TimeSpan? duration = file.TryGetProperty("length", out var l) &&
                                     l.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(l.GetInt32())
                    : null;

                var (artist, title) = SplitArtistTitle(filename);
                var sourceUrl = BuildSourceUrl(username, filename, size);
                candidates.Add(new DownloaderSearchHit(
                    Title: title,
                    Artist: artist,
                    SourceUrl: sourceUrl,
                    Duration: duration,
                    BitrateKbps: kbps,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        query.Artist, query.Title, hitArtist: artist, hitTitle: title)));
                scores[sourceUrl] = (container == AudioFormat.Flac ? 2 : 0) + (freeSlot ? 1 : 0);
            }
        }

        if (candidates.Count == 0) return null;

        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, candidates,
            tieBreaker: hit => scores.GetValueOrDefault(hit.SourceUrl ?? "", 0));
    }

    /// <summary>The declared container for a Soulseek filename, or null.</summary>
    internal static AudioFormat? ContainerFor(string filename)
    {
        var ext = Path.GetExtension(filename.Replace('\\', '/')).ToLowerInvariant();
        return ext switch
        {
            ".flac" => AudioFormat.Flac,
            ".mp3" => AudioFormat.Mp3,
            ".m4a" or ".mp4" or ".aac" => AudioFormat.Aac,
            _ => null,
        };
    }

    /// <summary>
    /// Best-effort artist/title from a Soulseek filename ("01 - Artist -
    /// Title.ext"): peers name files loosely, so the ranker gets what it
    /// can and the duration check does the rest.
    /// </summary>
    internal static (string? Artist, string Title) SplitArtistTitle(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename.Replace('\\', '/'));
        name = TrackNumberPrefix.Replace(name, "");
        var sep = name.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
            return (name[..sep].Trim(), name[(sep + 3)..].Trim());
        return (null, name.Trim());
    }

    /// <summary>The opaque reference the manager passes back for a download.</summary>
    internal static string BuildSourceUrl(string username, string filename, long size)
        => $"slskd:{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(filename)}:{size}";
}
