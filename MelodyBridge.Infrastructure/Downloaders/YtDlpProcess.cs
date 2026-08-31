using System.Diagnostics;

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
