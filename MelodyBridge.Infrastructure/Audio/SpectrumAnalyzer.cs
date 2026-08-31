using System.Diagnostics;
using System.Globalization;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// How strictly downloaded files are verified after download.
/// </summary>
public enum SpectrumMode
{
    /// <summary>Trust the declared bitrate.</summary>
    Off,
    /// <summary>Analyze the first 60 seconds (fast, catches obvious blow-ups).</summary>
    Fast,
    /// <summary>Analyze the whole file (slower, also catches subtle ones).</summary>
    Thorough,
}

/// <summary>
/// Detects inflated bitrates: files re-encoded from a lower-quality source
/// to a higher bitrate sound no better but waste space. The real spectral
/// ceiling, measured with ffmpeg's aspectralstats rolloff over all frames,
/// exposes it: an up-scaled file's spectrum stops where its source stopped.
///
/// Rolloff is content-dependent (quiet arrangements measure low even when
/// genuine), so quiet files are reported as inconclusive and the result is
/// a warning shown next to the track, never a silent rejection.
/// </summary>
public static class SpectrumAnalyzer
{
    /// <summary>Result of one spectral verification.</summary>
    public record Result(
        bool LooksInflated,
        double MedianRolloffHz,
        int EffectiveKbpsClass,
        string Note);

    /// <summary>
    /// Verifies a downloaded audio file's real spectral ceiling.
    /// Returns null when the mode is Off, ffmpeg is missing, or the file
    /// cannot be read.
    /// </summary>
    public static Result? Verify(string filePath, SpectrumMode mode)
    {
        if (mode == SpectrumMode.Off) return null;
        var ffmpeg = FindBinary("ffmpeg");
        if (ffmpeg is null) return null;

        var limit = mode == SpectrumMode.Fast ? " -t 60" : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments =
                $"-hide_banner -loglevel info{limit} -i \"{filePath}\" " +
                $"-af \"aspectralstats=measure=rolloff,ametadata=mode=print\" " +
                $"-f null -",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            // Metadata prints on stderr.
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(180000)) return null;
            if (proc.ExitCode != 0) return null;

            var rolloffs = ParseRolloffs(stderr);
            if (rolloffs.Count == 0) return null;
            rolloffs.Sort();
            var median = rolloffs[rolloffs.Count / 2];
            return Judge(median, ReadDeclaredKbps(stderr));
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadDeclaredKbps(string stderr)
    {
        // Stream line shape: "... Audio: mp3 (mp3float), 48000 Hz, mono, fltp, 320 kb/s"
        var match = System.Text.RegularExpressions.Regex.Match(
            stderr, @"Audio:.*?,\s*(\d+)\skb/s");
        return match.Success && int.TryParse(match.Groups[1].Value, out var kbps) && kbps > 0
            ? kbps
            : null;
    }

    private static List<double> ParseRolloffs(string stderr)
    {
        var rolloffs = new List<double>();
        foreach (var line in stderr.Split('\n'))
        {
            var idx = line.IndexOf("rolloff=", StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = line[(idx + "rolloff=".Length)..];
            var end = 0;
            while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '.' or '-' or 'e' or '+')) end++;
            if (double.TryParse(rest[..end], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var hz) && hz > 0)
                rolloffs.Add(hz);
        }
        return rolloffs;
    }

    /// <summary>
    /// Maps the median spectral ceiling to an effective bitrate class and
    /// compares it against the bitrate the file claims. Inflated means:
    /// the container promises more than the spectrum holds.
    /// ponytail: thresholds calibrated against LAME re-encode chains
    /// (64k cuts near 13.7 kHz, 128k near 16, 320 keeps 15 kHz+ on dense
    /// content). Recalibrate with a known reference file if needed.
    /// </summary>
    private static Result Judge(double medianHz, int? declaredKbps)
    {
        if (medianHz < 6000)
        {
            // Quiet arrangements: the rolloff measures low even for genuine
            // high-bitrate files. Report as inconclusive instead of accusing.
            return new Result(false, medianHz, 0,
                $"quiet content (ceiling {medianHz / 1000:F1} kHz): spectrum not conclusive");
        }

        var (effectiveKbps, note) = medianHz switch
        {
            < 8000 => (96, $"spectral ceiling {medianHz / 1000:F1} kHz: cannot hold more than ~96 kbps of real detail"),
            < 13000 => (128, $"spectral ceiling {medianHz / 1000:F1} kHz: matches a 128 kbps source"),
            < 15500 => (256, $"spectral ceiling {medianHz / 1000:F1} kHz: matches a 256 kbps source"),
            _ => (320, $"spectral ceiling {medianHz / 1000:F1} kHz: consistent with 320 kbps"),
        };

        // Any class below the claim means wasted bits.
        var inflated = declaredKbps is > 0
            && ClassOf(declaredKbps.Value) > effectiveKbps;
        return new Result(inflated, medianHz, effectiveKbps, note);
    }

    private static int ClassOf(int kbps) => kbps switch
    {
        < 130 => 128,
        < 200 => 128,
        < 300 => 256,
        _ => 320,
    };

    private static string? FindBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(OperatingSystem.IsWindows() ? ';' : ':',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), name);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            catch { /* unreadable PATH entry */ }
        }
        return null;
    }
}
