using System.Diagnostics;
using System.Text;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Runs a small external tool for the CLI-backed plugins (deemix,
/// qobuz-dlp): resolves the executable on PATH, captures stdout and
/// stderr, enforces a timeout and kills the process tree on expiry. The
/// yt-dlp plugin keeps its own runner because it parses yt-dlp's JSON
/// output; this one is the plain "run it and report" helper.
/// </summary>
public static class CliRunner
{
    public record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    /// <summary>
    /// Splits a configured command line into the executable and its fixed
    /// arguments; quoted segments stay together. Null when empty.
    /// </summary>
    public static (string Executable, IReadOnlyList<string> Arguments)? SplitCommand(string commandLine)
    {
        var parts = Tokenize(commandLine);
        if (parts.Count == 0) return null;
        return (parts[0], parts.Skip(1).ToList());
    }

    /// <summary>True when the executable can be started: a path or on PATH.</summary>
    public static bool Exists(string executable)
    {
        if (executable.Length == 0) return false;
        if (executable.Contains('/') || executable.Contains(Path.DirectorySeparatorChar))
            return File.Exists(executable);
        return FindOnPath(executable) is not null;
    }

    /// <summary>
    /// Runs the tool; a timeout or a missing executable comes back as a
    /// non-zero result with the reason in stderr, never an exception.
    /// The optional environment entries are added to the child process,
    /// which is how the plugins point the tools at their config folders.
    /// </summary>
    public static async Task<Result> RunAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var path = executable.Contains('/') || executable.Contains(Path.DirectorySeparatorChar)
            ? executable
            : FindOnPath(executable);
        if (path is null)
            return new Result(-1, "", $"'{executable}' was not found on PATH");

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
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", $"could not start '{executable}': {ex.Message}");
        }
        if (process is null)
            return new Result(-1, "", $"could not start '{executable}'");

        using var proc = process;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return new Result(proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(-1, "", $"the command did not finish within {timeout.TotalSeconds:0} seconds");
        }
        finally
        {
            // A caller that cancels must not leave the child running.
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone, or the handle is closed: nothing to stop.
            }
        }
    }

    /// <summary>
    /// The newest audio file under a folder, or null. The CLI tools write
    /// cover art and M3U files next to the audio, so the scan only
    /// accepts audio extensions and the caller sniffs the bytes anyway.
    /// </summary>
    public static string? FindNewestAudioFile(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        string[] extensions = [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav"];
        try
        {
            string? best = null;
            var bestWritten = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                var written = File.GetLastWriteTimeUtc(file);
                if (best is null || written > bestWritten)
                {
                    best = file;
                    bestWritten = written;
                }
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whitespace split with single and double quote support.</summary>
    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in line)
        {
            if (quote is not null)
            {
                if (ch == quote) quote = null;
                else current.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
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
