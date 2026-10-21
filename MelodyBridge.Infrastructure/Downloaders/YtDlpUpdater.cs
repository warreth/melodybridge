using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>Outcome of one yt-dlp update attempt.</summary>
public sealed record YtDlpUpdateResult(bool Updated, string Message);

/// <summary>
/// Reads and updates the yt-dlp binary behind the YouTube and SoundCloud
/// plugins. The image pins the version from its build time and YouTube
/// breaks old versions, so this is the on-demand way to get a newer one
/// without pulling a new image. Standalone installs update with
/// "yt-dlp -U"; pip installs (the container) fall through to pip, with the
/// externally-managed-environment flag when the distro asks for it.
/// </summary>
public class YtDlpUpdater
{
    /// <summary>Test seam: runs a program and returns exit code and output.</summary>
    internal delegate Task<(int Exit, string Stdout, string Stderr)> Runner(
        string program, IReadOnlyList<string> arguments, CancellationToken ct);

    private static readonly TimeSpan VersionTtl = TimeSpan.FromMinutes(10);

    private readonly ILogger<YtDlpUpdater> _logger;
    private readonly Func<string?> _binary;
    private readonly Runner _runner;
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private string? _cachedVersion;
    private long _versionReadAtTicks;

    public YtDlpUpdater(ILogger<YtDlpUpdater> logger)
        : this(logger, () => YtDlpProcess.BinaryPath, RunProcessAsync)
    {
    }

    internal YtDlpUpdater(ILogger<YtDlpUpdater> logger, Func<string?> binary, Runner runner)
    {
        _logger = logger;
        _binary = binary;
        _runner = runner;
    }

    /// <summary>
    /// Version from "yt-dlp --version", or null when not installed. Starting
    /// the yt-dlp process takes almost a second on some hosts, and the
    /// Plugins page asks on every visit, so the answer is cached for a short
    /// while and shared by callers that arrive together.
    /// </summary>
    public virtual async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        if (TryGetFresh(out var cached)) return cached;

        await _versionGate.WaitAsync(ct);
        try
        {
            // Another visitor may have filled the cache while this one waited.
            if (TryGetFresh(out var cachedInside)) return cachedInside;
            var version = await ReadVersionAsync(ct);
            CacheVersion(version);
            return version;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    /// <summary>The cached version while it is still fresh.</summary>
    private bool TryGetFresh(out string? version)
    {
        version = null;
        var readAt = Volatile.Read(ref _versionReadAtTicks);
        if (readAt == 0) return false;
        if (DateTimeOffset.UtcNow.UtcTicks - readAt >= VersionTtl.Ticks) return false;
        version = Volatile.Read(ref _cachedVersion);
        return true;
    }

    private void CacheVersion(string? version)
    {
        Volatile.Write(ref _cachedVersion, version);
        Volatile.Write(ref _versionReadAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>Runs "yt-dlp --version" without consulting the cache.</summary>
    private async Task<string?> ReadVersionAsync(CancellationToken ct)
    {
        var binary = _binary();
        if (binary is null) return null;
        try
        {
            var (exit, stdout, _) = await _runner(binary, new[] { "--version" }, ct);
            return exit == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the yt-dlp version failed");
            return null;
        }
    }

    /// <summary>
    /// Updates yt-dlp in place: its own updater first, then pip for
    /// python3, then python. Returns what happened for the UI to show.
    /// </summary>
    public virtual async Task<YtDlpUpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var binary = _binary();
            if (binary is not null)
            {
                var self = await _runner(binary, new[] { "-U" }, ct);
                if (self.Exit == 0)
                    return await SuccessAsync("yt-dlp updated itself.", ct);
            }

            foreach (var program in new[] { "python3", "python" })
            {
                var attempt = await TryPipAsync(program, withBreakFlag: false, ct);
                if (!attempt.Ran) continue;
                if (attempt.Exit == 0)
                    return await SuccessAsync($"Updated with {program} -m pip.", ct);

                // Debian and friends block system pip installs unless the
                // caller opts in; pip says so in the error text.
                if (attempt.Stderr.Contains("externally-managed-environment", StringComparison.Ordinal))
                {
                    var forced = await TryPipAsync(program, withBreakFlag: true, ct);
                    if (forced.Ran && forced.Exit == 0)
                        return await SuccessAsync($"Updated with {program} -m pip.", ct);
                    return new YtDlpUpdateResult(false, Tail(forced.Stderr));
                }

                return new YtDlpUpdateResult(false, Tail(attempt.Stderr));
            }

            return new YtDlpUpdateResult(false,
                "Nothing to update with: yt-dlp, python3 and python are all missing.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp update failed");
            return new YtDlpUpdateResult(false, ex.Message);
        }
    }

    /// <summary>Runs "python -m pip install --upgrade [--break-system-packages] yt-dlp".</summary>
    private async Task<(bool Ran, int Exit, string Stderr)> TryPipAsync(
        string program, bool withBreakFlag, CancellationToken ct)
    {
        var arguments = new List<string> { "-m", "pip", "install", "--upgrade" };
        if (withBreakFlag) arguments.Add("--break-system-packages");
        arguments.Add("yt-dlp");
        try
        {
            var (exit, _, stderr) = await _runner(program, arguments, ct);
            return (true, exit, stderr);
        }
        catch (Win32Exception)
        {
            return (false, -1, ""); // program not on PATH: try the next one
        }
    }

    private async Task<YtDlpUpdateResult> SuccessAsync(string how, CancellationToken ct)
    {
        // The update just replaced the binary, so this read must bypass the
        // cached version and replace it with the new one.
        var version = await ReadVersionAsync(ct);
        CacheVersion(version);
        var message = version is null ? how : $"{how} Now on {version}.";
        _logger.LogInformation("yt-dlp update succeeded: {Message}", message);
        return new YtDlpUpdateResult(true, message);
    }

    /// <summary>Last part of an error, bounded for the UI.</summary>
    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunProcessAsync(
        string program, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {program}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"{program} did not finish within 5 minutes");
        }
    }
}
