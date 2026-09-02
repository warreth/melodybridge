using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// Measures the real bitrate of a local audio file with ffprobe.
/// Used to enforce the requested quality cap on every completed
/// download, whatever the plugin claimed.
/// </summary>
public static partial class BitrateProbe
{
    [GeneratedRegex(@"""bit_rate"":\s*""?([0-9]+)""?")]
    private static partial Regex BitRateRegex();

    /// <summary>Real average bitrate in kbps, or null when ffprobe fails.</summary>
    /// <remarks>MP3 reports the bitrate on the audio stream; FLAC and Opus
    /// only expose it on the container format, so both places are probed in
    /// one run: stream value wins, format value is the fallback.</remarks>
    public static int? MeasureKbps(string filePath)
    {
        var ffprobe = SpectrumAnalyzer.FindFfprobe();
        if (ffprobe is null) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                // No shell involved: pass the path unquoted (quotes would be
                // taken literally and ffprobe would not find the file).
                Arguments = $"-v error -select_streams a:0 "
                    + "-show_entries stream=bit_rate -show_entries format=bit_rate "
                    + $"-of json {filePath}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(15000)) return null;

            // json output nests streams[] and format{}: the first hit is the
            // stream value, the second (if any) the format fallback.
            var matches = BitRateRegex().Matches(stdout);
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups[1].Value, out var bps) && bps > 0)
                    return bps / 1000;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
