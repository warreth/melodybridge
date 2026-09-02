using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>Verdict of a <see cref="FileIntegrity"/> check.</summary>
/// <param name="Ok">True when ffprobe can parse the file and its duration is sane.</param>
/// <param name="Reason">Terse failure reason; null when <paramref name="Ok"/> is true.</param>
public record IntegrityResult(bool Ok, string? Reason);

/// <summary>
/// Fast post-download integrity check: parses the file with ffprobe and
/// compares its duration against the expected one (what the source
/// platform advertised for the track). Truncated or corrupt downloads
/// either fail to parse at all or report a duration far below the
/// expectation, and are deleted by the caller so the next run retries.
/// </summary>
public static class FileIntegrity
{
    /// <summary>
    /// Checks a downloaded audio file. Ok when ffprobe exits 0, the
    /// duration parses to a positive value, and it is within
    /// max(30s, 10% of the expected duration) when one is given.
    /// </summary>
    public static IntegrityResult Check(string path, TimeSpan? expectedDuration = null)
    {
        var ffprobe = SpectrumAnalyzer.FindFfprobe();
        if (ffprobe is null)
            return new(false, "ffprobe not found");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                // No shell involved: the path goes unquoted like BitrateProbe does.
                Arguments = $"-v error -show_entries format=duration -of json {path}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new(false, "ffprobe failed");
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(15000)) return new(false, "ffprobe failed");

            if (proc.ExitCode != 0)
                return new(false, "ffprobe failed");

            var duration = ParseDuration(stdout);
            if (duration is null or <= 0)
                return new(false, "no duration");

            if (expectedDuration is { } expected)
            {
                var toleranceSeconds = Math.Max(30, expected.TotalSeconds * 0.1);
                if (Math.Abs(duration.Value - expected.TotalSeconds) > toleranceSeconds)
                    return new(false,
                        $"duration mismatch: {duration.Value:0.##}s vs expected {expected.TotalSeconds:0.##}s");
            }

            return new(true, null);
        }
        catch
        {
            return new(false, "ffprobe failed");
        }
    }

    /// <summary>Reads format.duration from ffprobe's JSON ("N/A" and missing mean no duration).</summary>
    private static double? ParseDuration(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("format", out var format)) return null;
            if (!format.TryGetProperty("duration", out var duration)) return null;
            var raw = duration.GetString();
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch
        {
            return null;
        }
    }
}
