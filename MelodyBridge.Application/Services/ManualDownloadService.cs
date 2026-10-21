using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

/// <summary>
/// One song download outside a playlist: a search across every enabled
/// plugin, a hand-picked candidate, or a direct link. The download itself
/// goes through the same manager and plugins as a playlist track, so the
/// audio sanity and MELODY_ID tag still apply; the extra work here is
/// naming and tagging the file for a library that has no track row yet.
/// </summary>
public sealed class ManualDownloadService
{
    private readonly IDownloadManager _downloads;
    private readonly IDownloaderRegistry _registry;
    private readonly IUrlMetadataResolver _metadata;
    private readonly ILogger<ManualDownloadService> _logger;

    public ManualDownloadService(
        IDownloadManager downloads,
        IDownloaderRegistry registry,
        IUrlMetadataResolver metadata,
        ILogger<ManualDownloadService> logger)
    {
        _downloads = downloads;
        _registry = registry;
        _metadata = metadata;
        _logger = logger;
    }

    /// <summary>
    /// Every candidate the enabled plugins offer for "Artist - Title" or
    /// a bare title, streamed as each plugin answers. No acceptance gate:
    /// the human is the gate here.
    /// </summary>
    public Task<SourceSearchOutcome> SearchAsync(
        string rawQuery, DownloadQuality quality,
        Action<DownloadCandidate>? onCandidate = null, CancellationToken ct = default)
    {
        var (artist, title) = ParseQuery(rawQuery);
        if (title.Length == 0)
        {
            return Task.FromResult(new SourceSearchOutcome(
                Array.Empty<DownloadCandidate>(), Array.Empty<SourceSearchProblem>()));
        }

        return _downloads.SearchCandidatesDetailedAsync(
            new TrackQuery(artist, title, quality), onCandidate, ct);
    }

    /// <summary>
    /// The exact source facts for one candidate, for the picker's quality
    /// line. Best effort: a plugin that cannot inspect the link returns null.
    /// </summary>
    public Task<CandidateAudioQuality?> ProbeAsync(
        DownloadCandidate candidate, CancellationToken ct = default)
        => _downloads.ProbeQualityAsync(candidate.PluginId, candidate.SourceUrl, ct);

    /// <summary>Downloads one candidate picked from <see cref="SearchAsync"/>.</summary>
    public async Task<ManualDownloadResult> DownloadCandidateAsync(
        string rawQuery, DownloadCandidate candidate, string? targetDirectory,
        DownloadQuality quality, CancellationToken ct = default)
    {
        var (artist, title) = ParseQuery(rawQuery);
        if (title.Length == 0)
            return ManualDownloadResult.Failed("Type a title first.");
        return await DownloadCandidateCoreAsync(
            candidate, artist, title, quality, targetDirectory, candidate.Duration, ct);
    }

    /// <summary>
    /// Downloads a link as-is through the enabled plugins. Artist and title
    /// are optional: when one is missing the link's own metadata fills it,
    /// and a URL-shaped fallback names the file when even that is absent.
    /// </summary>
    public async Task<ManualDownloadResult> DownloadUrlAsync(
        string url, string? artist, string? title, string? targetDirectory,
        CancellationToken ct = default)
    {
        if (!TryParseLink(url, out var uri))
            return ManualDownloadResult.Failed("Paste a full http or https link.");

        UrlTrackMetadata? resolved = null;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            resolved = await _metadata.ResolveAsync(uri.ToString(), ct);
        }

        var finalArtist = FirstNonEmpty(artist, resolved?.Artist);
        var finalTitle = FirstNonEmpty(title, resolved?.Title, DeriveTitle(uri));
        if (string.IsNullOrWhiteSpace(title))
        {
            // YouTube and SoundCloud titles often repeat the artist. The
            // display title and the file name should not say it twice.
            finalTitle = StripArtistPrefix(finalTitle, finalArtist);
        }
        var directory = ResolveDirectory(targetDirectory);
        if (directory.Error is { Length: > 0 } directoryError)
            return ManualDownloadResult.Failed(directoryError);

        var melodyId = NewMelodyId();
        ReportProgress(melodyId, finalTitle, "downloading", null, null);
        string? path;
        try
        {
            path = await _downloads.DownloadAsync(uri.ToString(), directory.Path!, melodyId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Manual link download failed for {Url}", uri);
            ReportProgress(melodyId, finalTitle, "failed", null, null, warning: ex.Message);
            return ManualDownloadResult.Failed(ex.Message);
        }

        if (path is null)
        {
            var reason = _downloads.LastFailure(melodyId)
                ?? "No enabled plugin could download this link. Try another link or search by name.";
            ReportProgress(melodyId, finalTitle, "failed", null, null, warning: reason);
            return ManualDownloadResult.Failed(reason);
        }

        var result = Finish(path, finalArtist, finalTitle, melodyId, resolved?.Duration, directory.Path!);
        ReportProgress(melodyId, finalTitle, result.Success ? "done" : "failed",
            result.PluginName, result.FilePath,
            warning: result.Success ? result.Warning : result.Error);
        return result;
    }

    private async Task<ManualDownloadResult> DownloadCandidateCoreAsync(
        DownloadCandidate candidate, string? artist, string title,
        DownloadQuality quality, string? targetDirectory,
        TimeSpan? expectedDuration, CancellationToken ct)
    {
        var directory = ResolveDirectory(targetDirectory);
        if (directory.Error is { Length: > 0 } directoryError)
            return ManualDownloadResult.Failed(directoryError);

        var melodyId = NewMelodyId();
        var query = new TrackQuery(artist ?? string.Empty, title, quality);
        string? path;
        try
        {
            path = await _downloads.DownloadCandidateAsync(
                candidate, query, directory.Path!, melodyId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Manual candidate download failed for {Title}", title);
            ReportProgress(melodyId, title, "failed", candidate.PluginName, null,
                candidate.Confidence, ex.Message);
            return ManualDownloadResult.Failed(ex.Message);
        }

        if (path is null)
        {
            var reason = _downloads.LastFailure(melodyId)
                ?? $"{candidate.PluginName} did not deliver this pick.";
            ReportProgress(melodyId, title, "failed", candidate.PluginName, null,
                candidate.Confidence, reason);
            return ManualDownloadResult.Failed(reason);
        }

        var result = Finish(path, artist, title, melodyId, expectedDuration, directory.Path!);
        ReportProgress(melodyId, title, result.Success ? "done" : "failed",
            result.PluginName, result.FilePath, candidate.Confidence,
            result.Success ? result.Warning : result.Error);
        return result;
    }

    /// <summary>Mirrors a manual attempt into the live overview.</summary>
    private void ReportProgress(string melodyId, string title, string status,
        string? plugin, string? path, MatchConfidence confidence = MatchConfidence.High,
        string? warning = null)
        => _downloads.ReportProgress(new DownloadProgress(
            melodyId, title, status, plugin, path, confidence, Warning: warning));

    /// <summary>
    /// Tags, sanity-checks and names the file. A corrupt file is removed;
    /// a length mismatch or a missing ffprobe keeps the file and says so,
    /// because the bytes are still playable and the user made the pick.
    /// </summary>
    private ManualDownloadResult Finish(
        string path, string? artist, string title, string melodyId,
        TimeSpan? expectedDuration, string directory)
    {
        string? warning = null;
        try
        {
            var integrity = FileIntegrity.Check(path, expectedDuration);
            if (integrity.ToolMissing)
            {
                warning = "ffprobe is not installed, so the file length was not verified.";
            }
            else if (integrity.Issue == IntegrityIssue.Corrupt)
            {
                TryDelete(path);
                return ManualDownloadResult.Failed(
                    $"The downloaded file is corrupt and was removed: {integrity.Reason}");
            }
            else if (integrity.Issue == IntegrityIssue.DurationMismatch)
            {
                warning = $"The file length differs from the source: {integrity.Reason}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check manual download {Path}", path);
        }

        try
        {
            TaglibHelper.WriteTags(path, title: title, artist: artist);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tag manual download {Path}", path);
        }

        try
        {
            // The identity is what the scanner matches on, so write it
            // after the title/artist rewrite and never skip it silently.
            TaglibHelper.WriteMelodyId(path, melodyId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stamp the manual download {Path}", path);
        }

        var extension = Path.GetExtension(path);
        var baseName = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        var used = Directory.EnumerateFiles(directory).ToList();
        var destination = SafeFileNames.UniqueFile(directory, baseName, extension, used);
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Move(path, destination);
                path = destination;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not rename manual download {Path}", path);
                warning = AppendWarning(warning,
                    "The file kept its temporary name because renaming failed.");
            }
        }

        var pluginId = _downloads.LastPluginId(melodyId);
        var pluginName = pluginId is { Length: > 0 }
            ? _registry.Get(pluginId)?.Name ?? pluginId
            : null;
        return new ManualDownloadResult(true, path, pluginName, null, warning);
    }

    /// <summary>
    /// Splits the search box. "Artist - Title" gives the plugins both
    /// fields; a bare phrase stays a title so a one-word search still
    /// works instead of turning into an empty query.
    /// </summary>
    public static (string Artist, string Title) ParseQuery(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return (string.Empty, string.Empty);
        var separator = text.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && separator < text.Length - 3)
        {
            var artist = text[..separator].Trim();
            var title = text[(separator + 3)..].Trim();
            if (artist.Length > 0 && title.Length > 0) return (artist, title);
        }
        return (string.Empty, text);
    }

    /// <summary>True for a full http(s) link; anything else is a search.</summary>
    public static bool TryParseLink(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = parsed;
        return true;
    }

    private static string DeriveTitle(Uri uri)
    {
        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        var name = string.IsNullOrWhiteSpace(segment)
            ? uri.Host
            : Uri.UnescapeDataString(segment);
        name = Path.GetFileNameWithoutExtension(name)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
        if (name.Length == 0
            || name.Equals("watch", StringComparison.OrdinalIgnoreCase)
            || name.Equals("track", StringComparison.OrdinalIgnoreCase))
        {
            return "Downloaded track";
        }
        return name;
    }

    private static (string? Path, string? Error) ResolveDirectory(string? targetDirectory)
    {
        var trimmed = targetDirectory?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return (null, "Choose a folder to save into.");
        if (!Path.IsPathRooted(trimmed))
            return (null, "Use an absolute folder path.");
        try
        {
            Directory.CreateDirectory(trimmed);
        }
        catch (Exception ex)
        {
            return (null, $"Could not create '{trimmed}': {ex.Message}");
        }
        return (trimmed, null);
    }

    private static string NewMelodyId() => $"manual-{Guid.NewGuid():N}";

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    /// <summary>
    /// "Daft Punk - Get Lucky" from a source whose artist is also Daft Punk
    /// becomes "Get Lucky", so the artist is not printed twice.
    /// </summary>
    private static string StripArtistPrefix(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return title;
        var name = artist.Trim();
        if (!title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return title;
        var rest = title[name.Length..].TrimStart();
        if (rest.Length == 0 || rest[0] != '-') return title;
        rest = rest[1..].TrimStart();
        return rest.Length > 0 ? rest : title;
    }

    private static string AppendWarning(string? existing, string next)
        => string.IsNullOrWhiteSpace(existing) ? next : $"{existing} {next}";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* the file is corrupt either way */ }
    }
}

/// <summary>What one manual download produced, and what to say about it.</summary>
public sealed record ManualDownloadResult(
    bool Success,
    string? FilePath = null,
    string? PluginName = null,
    string? Error = null,
    string? Warning = null)
{
    public static ManualDownloadResult Failed(string error)
        => new(false, Error: error);
}
