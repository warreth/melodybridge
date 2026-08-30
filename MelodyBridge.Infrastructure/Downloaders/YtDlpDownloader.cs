using System.Diagnostics;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// yt-dlp downloader plugin.
///
/// Searches YouTube Music first (falls back to plain YouTube) with
/// "artist title" queries, downloads the best audio as MP3, and writes
/// MELODY_ID + artist/title metadata tags into the file.
/// </summary>
public class YtDlpDownloader : IDownloader
{
    private readonly ILogger<YtDlpDownloader> _logger;
    private readonly string _ytDlpPath;

    public string Id => "ytdlp";
    public string Name => "yt-dlp (YouTube)";

    public YtDlpDownloader(ILogger<YtDlpDownloader> logger)
    {
        _logger = logger;
        _ytDlpPath = ResolveBinary();
    }

    /// <summary>Internal constructor for tests to pin a specific binary path.</summary>
    internal YtDlpDownloader(ILogger<YtDlpDownloader> logger, string ytDlpPath)
        : this(logger)
    {
        _ytDlpPath = ytDlpPath;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // A missing binary fails fast here so the waterfall can skip us.
        return Task.FromResult(_ytDlpPath is not null);
    }

    public async Task<DownloaderSearchHit?> SearchAsync(string artist, string title, CancellationToken ct = default)
    {
        if (_ytDlpPath is null) return null;

        var query = $"{artist} {title}".Trim();
        if (query.Length == 0) return null;

        // Try YouTube Music first for better audio matches; fall back to YouTube.
        foreach (var extractor in new[] { "ytmsearch1", "ytsearch1" })
        {
            try
            {
                var (exit, stdout, _) = await RunAsync(new[]
                {
                    $"{extractor}:{query}",
                    "--flat-playlist",
                    "--dump-single-json",
                    "--no-warnings",
                }, TimeSpan.FromSeconds(45), ct);

                if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) continue;

                using var doc = JsonDocument.Parse(stdout);
                var entry = doc.RootElement.TryGetProperty("entries", out var entries)
                    && entries.ValueKind == JsonValueKind.Array
                    && entries.GetArrayLength() > 0
                    ? entries[0]
                    : doc.RootElement.TryGetProperty("url", out _)
                        ? doc.RootElement
                        : default;

                if (entry.ValueKind == JsonValueKind.Undefined) continue;

                var id = entry.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                return new DownloaderSearchHit(
                    Title: entry.TryGetProperty("title", out var t) ? t.GetString() : title,
                    Artist: artist,
                    SourceUrl: $"https://www.youtube.com/watch?v={id}",
                    Duration: entry.TryGetProperty("duration", out var d) && d.TryGetDouble(out var secs)
                        ? TimeSpan.FromSeconds(secs)
                        : null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "yt-dlp search via {Extractor} failed for '{Query}'", extractor, query);
            }
        }

        return null;
    }

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        CancellationToken ct = default)
    {
        if (_ytDlpPath is null)
            return new DownloaderDownloadResult(false, null, "yt-dlp binary not found");

        try
        {
            Directory.CreateDirectory(outputDirectory);

            // Deterministic filename so re-downloads replace instead of duplicate.
            var template = Path.Combine(outputDirectory, "%(id)s.%(ext)s");

            var (exit, stdout, stderr) = await RunAsync(new[]
            {
                "-x",
                "--audio-format", "mp3",
                "--audio-quality", "0",
                "--embed-metadata",
                "-o", template,
                sourceUrl,
                "--print-json",
                "--no-simulate",
            }, TimeSpan.FromMinutes(5), ct);

            if (exit != 0)
                return new DownloaderDownloadResult(false, null, $"yt-dlp exited {exit}: {Truncate(stderr, 400)}");

            // Locate the produced file (id from JSON confirms which one is ours).
            var file = FindProducedFile(stdout, outputDirectory);
            if (file is null)
                return new DownloaderDownloadResult(false, null, "yt-dlp reported success but no output file found");

            if (melodyId is not null)
                Tagging.TaglibHelper.WriteMelodyId(file, melodyId);

            return new DownloaderDownloadResult(true, file, null);
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    private static string? FindProducedFile(string jsonOutput, string outputDirectory)
    {
        // --print-json's "filename" points at the pre-extraction file (e.g. .webm);
        // the real output is {template}_{id}.mp3. Match by video ID + audio ext.
        string? videoId = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                videoId = id.GetString();
        }
        catch
        {
            // JSON parse failed (e.g. warnings on stdout) — fall through.
        }

        if (videoId is not null)
        {
            var byId = Directory.EnumerateFiles(outputDirectory)
                .FirstOrDefault(p =>
                    Path.GetFileName(p).Contains(videoId, StringComparison.Ordinal) &&
                    IsAudioExtension(Path.GetExtension(p)));
            if (byId is not null) return byId;
        }

        // Fallback: newest audio file in the directory.
        return Directory.EnumerateFiles(outputDirectory)
            .Where(p => IsAudioExtension(Path.GetExtension(p)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsAudioExtension(string? ext)
        => ext is not null && ext.ToLowerInvariant() is ".mp3" or ".m4a" or ".opus" or ".ogg" or ".flac" or ".wav" or ".webm";

    private async Task<(int exit, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout, not the caller's cancellation.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"yt-dlp did not finish within {timeout.TotalSeconds}s");
        }
    }

    private static string? ResolveBinary()
    {
        foreach (var name in new[] { "yt-dlp", "yt-dlp_linux", "youtube-dl" })
        {
            var found = FindOnPath(name);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        // Windows appends .exe implicitly; on Linux the plain name resolves.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ':', ';' };
        foreach (var dir in pathEnv.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                var fullPath = OperatingSystem.IsWindows() ? candidate + ".exe" : candidate;
                if (File.Exists(fullPath)) return fullPath;
                if (!OperatingSystem.IsWindows() && File.Exists(candidate)) return candidate;
            }
            catch { /* unreadable PATH entry */ }
        }
        return null;
    }


    private static string Truncate(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
