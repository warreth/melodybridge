using System.Diagnostics;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Shared yt-dlp process plumbing: binary resolution on PATH and a
/// cancellable, timeout-guarded argument-list runner used by all
/// yt-dlp-backed downloader plugins.
/// </summary>
internal static class YtDlpProcess
{
    private static readonly Lazy<string?> Binary = new(ResolveBinary);

    /// <summary>Resolved yt-dlp binary path, or null when not installed.</summary>
    public static string? BinaryPath => Binary.Value;

    /// <summary>
    /// Download arguments shared by the yt-dlp-backed plugins. Always
    /// audio-only: yt-dlp's default format chain is
    /// bestvideo*+bestaudio, which downloads full video files (tens of
    /// megabytes) for a music library that only needs the audio stream
    /// (a few megabytes). Explicit formats add the transcode flags.
    /// </summary>
    public static List<string> BuildDownloadArgs(string template, string sourceUrl, DownloadQuality quality)
    {
        var args = new List<string>
        {
            "-f", "bestaudio/best",
            "-o", template,
            sourceUrl,
            "--print-json",
            "--no-simulate",
            // A watch URL carrying a list parameter must download the
            // track, never the whole playlist.
            "--no-playlist",
            "--socket-timeout", "20",
            "--retries", "3",
            "--fragment-retries", "3",
        };
        if (quality.NeedsTranscode)
        {
            args.InsertRange(0, new[]
            {
                "-x",
                "--audio-format", AudioFormatArg(quality.Format),
                "--audio-quality", quality.YtDlpAudioQuality,
                "--embed-metadata",
            });
        }
        return args;
    }

    /// <summary>yt-dlp's --audio-format name for the requested container.</summary>
    private static string AudioFormatArg(AudioFormat format) => format switch
    {
        AudioFormat.Aac => "m4a", // yt-dlp names the AAC container m4a
        AudioFormat.Opus => "opus",
        AudioFormat.Flac => "flac",
        _ => "mp3",
    };

    /// <summary>
    /// Runs a download with one retry on a transient failure (YouTube 403s
    /// and timeouts happen under parallel downloads; a fresh extraction
    /// usually hands out new media URLs). Caller cancellation and real
    /// errors propagate immediately.
    /// </summary>
    public static async Task<(int exit, string stdout, string stderr)> RunDownloadAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var first = await RunAsync(arguments, timeout, ct);
        if (first.exit == 0 || !IsTransientFailure(first.stderr))
            return first;

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        return await RunAsync(arguments, timeout, ct);
    }

    /// <summary>Media-URL 403/429/5xx and connection timeouts are worth a second try.</summary>
    internal static bool IsTransientFailure(string stderr)
        => stderr.Contains("HTTP Error 403", StringComparison.Ordinal)
        || stderr.Contains("HTTP Error 429", StringComparison.Ordinal)
        || stderr.Contains("HTTP Error 5", StringComparison.Ordinal)
        || stderr.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Temporary failure", StringComparison.OrdinalIgnoreCase);

    public static async Task<(int exit, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var path = BinaryPath
            ?? throw new InvalidOperationException("yt-dlp binary not found");

        var psi = new ProcessStartInfo
        {
            FileName = path,
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
}
