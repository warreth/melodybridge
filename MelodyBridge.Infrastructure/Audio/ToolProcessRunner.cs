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

    /// <summary>
    /// Runs the binary and hands its stdout stream to
    /// <paramref name="consume"/> while the process runs, so a large
    /// decode (a whole song of PCM) never has to be buffered whole.
    /// Returns true only on a clean exit before the timeout.
    /// </summary>
    internal static bool RunToStream(string binary, IReadOnlyList<string> arguments,
        int timeoutMs, Action<Stream> consume)
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

        // Consume the stream off the calling thread so the timeout below is
        // a real one even while the tool is still producing output.
        var consumeTask = Task.Run(() => consume(proc.StandardOutput.BaseStream));
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { proc.WaitForExit(5000); } catch { /* best effort */ }
            try { consumeTask.Wait(5000); } catch { /* consumer sees the closed pipe */ }
            return false;
        }

        consumeTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        return proc.ExitCode == 0;
    }
}
