using System.Globalization;
using System.Text.Json;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>What kind of problem a <see cref="FileIntegrity"/> check found.</summary>
public enum IntegrityIssue
{
    /// <summary>Everything checks out.</summary>
    None,
    /// <summary>Unreadable or truncated bytes: the file is broken, not just different.</summary>
    Corrupt,
    /// <summary>The file parses fine but its length does not match the expectation.</summary>
    DurationMismatch,
}

/// <summary>Verdict of a <see cref="FileIntegrity"/> check.</summary>
/// <param name="Ok">True when ffprobe can parse the file and its duration is sane.</param>
/// <param name="Reason">Terse failure reason; null when <paramref name="Ok"/> is true.</param>
/// <param name="Issue">The problem class, so the caller can tell a broken
/// file (delete it) from a wrong-length one (flag it and let the user
/// decide).</param>
/// <param name="ToolMissing">True when the check could not run because
/// ffprobe is not installed. That is not proof of corruption: the caller
/// keeps the file and flags it as unverified.</param>
/// <param name="ToolFailed">True when ffprobe could not start or timed
/// out. That is not proof of corruption either: the caller keeps the file
/// and flags it as unverified.</param>
public record IntegrityResult(
    bool Ok, string? Reason, IntegrityIssue Issue = IntegrityIssue.None,
    bool ToolMissing = false, bool ToolFailed = false);

/// <summary>
/// Fast post-download integrity check: parses the file with ffprobe and
/// compares its duration against the expected one (what the source
/// platform advertised for the track). Truncated or corrupt downloads
/// either fail to parse at all or report a duration far below the
/// expectation. The caller deletes broken files; a mere length mismatch
/// stays on disk and becomes a review the user settles.
/// </summary>
public static class FileIntegrity
{
    /// <summary>Default absolute tolerance when the caller passes none.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checks a downloaded audio file. Ok when ffprobe exits 0, the
    /// duration parses to a positive value, and it is within
    /// <paramref name="tolerance"/> of the expected duration. Without an
    /// explicit tolerance the window is max(30s, 10% of the expectation).
    /// A mismatch reports <see cref="IntegrityIssue.DurationMismatch"/> so
    /// the caller can flag the file instead of deleting it.
    /// </summary>
    public static IntegrityResult Check(
        string path, TimeSpan? expectedDuration = null, TimeSpan? tolerance = null)
    {
        var (seconds, failure, toolMissing, toolFailed) = ProbeDuration(path);
        if (toolMissing)
            return new(false, failure, IntegrityIssue.None, ToolMissing: true);
        if (failure is not null)
            return new(false, failure, IntegrityIssue.Corrupt, ToolFailed: toolFailed);
        if (seconds is not > 0)
            return new(false, "no duration", IntegrityIssue.Corrupt);

        if (expectedDuration is { } expected)
        {
            var allowed = tolerance ?? TimeSpan.FromSeconds(
                Math.Max(DefaultTolerance.TotalSeconds, expected.TotalSeconds * 0.1));
            if (Math.Abs(seconds.Value - expected.TotalSeconds) > allowed.TotalSeconds)
                return new(false,
                    $"duration mismatch: {seconds.Value:0.##}s vs expected {expected.TotalSeconds:0.##}s",
                    IntegrityIssue.DurationMismatch);
        }

        return new(true, null);
    }

    /// <summary>
    /// ffprobe duration of a local file, or null when it cannot be
    /// measured. The archive copier compares a transcode against its
    /// primary with this, so a flagged wrong-length primary does not
    /// block its own archive copy.
    /// </summary>
    public static TimeSpan? MeasureDuration(string path)
        => ProbeDuration(path).Seconds is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>
    /// One ffprobe run: the parsed duration in seconds, or the terse
    /// failure that explains why there is none.
    /// </summary>
    private static (double? Seconds, string? Failure, bool ToolMissing, bool ToolFailed)
        ProbeDuration(string path)
    {
        var ffprobe = SpectrumAnalyzer.FindFfprobe();
        if (ffprobe is null)
            return (null, "ffprobe not found", true, false);

        try
        {
            // ArgumentList keeps a path with spaces a single argument.
            var result = ToolProcessRunner.Run(ffprobe, new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "json",
                path,
            }, timeoutMs: 15000);
            if (result.TimedOut) return (null, "ffprobe timed out", false, true);
            if (result.ExitCode != 0)
            {
                // A parse failure is the file's verdict. Anything else
                // (a resource limit, a killed process, an I/O error) is
                // the tool's failure: the file stays and the caller flags
                // it as unverified.
                var parseFailure = LooksLikeParseFailure(result.Stderr);
                return (null, "ffprobe failed", false, !parseFailure);
            }
            return (ParseDuration(result.Stdout), null, false, false);
        }
        catch
        {
            // The process could not start (for example EMFILE): the tool
            // failed, not the file.
            return (null, "ffprobe failed", false, true);
        }
    }

    /// <summary>
    /// ffprobe's stderr for a file it cannot parse. A non-zero exit with
    /// none of these is treated as a tool failure, not a corrupt file:
    /// keeping a good file with an unverified flag is the safer mistake.
    /// </summary>
    internal static bool LooksLikeParseFailure(string stderr)
    {
        string[] signatures =
        {
            "Invalid data found when processing input",
            "Failed to read frame size",
            "moov atom not found",
            "could not find codec parameters",
            "Invalid argument",
            "End of file",
        };
        return signatures.Any(signature =>
            stderr.Contains(signature, StringComparison.OrdinalIgnoreCase));
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
