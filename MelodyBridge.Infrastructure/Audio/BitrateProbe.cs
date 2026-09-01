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
                Arguments = $"-v error -select_streams a:0 -show_entries stream=bit_rate -of json {filePath}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(15000)) return null;

            var match = BitRateRegex().Match(stdout);
            return match.Success && int.TryParse(match.Groups[1].Value, out var bps) && bps > 0
                ? bps / 1000
                : null;
        }
        catch
        {
            return null;
        }
    }
}
