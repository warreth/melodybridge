using System.Diagnostics;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// The one place the audio helpers start a tool process (ffprobe,
/// ffmpeg). Two bugs live here on purpose, fixed once for every helper:
///
///  - Arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/>,
///    never spliced into a raw command string. A path with a space
///    (C:\Users\First Last\Music) used to split into several arguments,
///    ffprobe failed, and the caller deleted the just-downloaded file as
///    "corrupt".
///  - stdout and stderr are drained asynchronously while the process
///    runs, so the timeout is real. Reading a pipe synchronously before
///    WaitForExit blocked forever on a hung tool and the timeout never
///    fired.
/// </summary>
internal static class ToolProcessRunner
{
    /// <summary>Outcome of one tool run. A timed-out run killed the process tree.</summary>
    internal readonly record struct Result(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>
    /// Runs the binary with the given arguments and a hard timeout.
    /// Throws when the process cannot start; callers already guard that.
    /// </summary>
    internal static Result Run(string binary, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"{binary} failed to start");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { proc.WaitForExit(5000); } catch { /* best effort */ }
            return new Result(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new Result(proc.ExitCode, stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult(), TimedOut: false);
    }
}
