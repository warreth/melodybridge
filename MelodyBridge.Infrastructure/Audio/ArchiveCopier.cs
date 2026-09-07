using System.Diagnostics;
using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>Outcome of one archive attempt. Never throws: a failed
/// archive copy is a warning on the track, not a failed download.</summary>
/// <param name="Ok">True when the archive file exists at the target path.</param>
/// <param name="ArchivePath">The path written, or the intended one on failure.</param>
/// <param name="Reason">Terse failure reason; null when <paramref name="Ok"/> is true.</param>
public record ArchiveResult(bool Ok, string ArchivePath, string? Reason);

/// <summary>
/// Produces the secondary archive copy of an already downloaded track:
/// a byte-for-byte copy when the archive format is Auto, or one local
/// ffmpeg transcode into the requested container. One internet download,
/// two library files. Every failure (missing ffmpeg, full disk, bad
/// target path) degrades to a track warning; the primary file is never
/// touched and never at risk.
/// </summary>
public static class ArchiveCopier
{
    /// <summary>
    /// Copies or transcodes <paramref name="primaryPath"/> into
    /// <paramref name="archiveDirectory"/> using the container implied by
    /// <paramref name="archiveFormat"/> ("auto" copies the file as-is).
    /// The output keeps the primary's filename with the archive format's
    /// extension, so re-runs replace instead of duplicate.
    /// </summary>
    /// <param name="melodyId">Written into the archive copy when the
    /// transcode dropped the MELODY_ID tag, same rule as the primary path.</param>
    /// <param name="title">Passed to the tag rewrite when it must run.</param>
    /// <param name="artist">Passed to the tag rewrite when it must run.</param>
    /// <param name="album">Passed to the tag rewrite when it must run.</param>
    public static ArchiveResult Copy(
        string primaryPath,
        string archiveDirectory,
        string? archiveFormat,
        string? melodyId,
        string? title,
        string? artist,
        string? album,
        uint? trackNumber,
        TimeSpan? expectedDuration = null)
    {
        var targetName = BuildTargetName(primaryPath, archiveFormat);
        var targetPath = Path.Combine(archiveDirectory, targetName);

        try
        {
            Directory.CreateDirectory(archiveDirectory);

            if (string.IsNullOrWhiteSpace(archiveFormat)
                || archiveFormat.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // Auto archive: byte-for-byte copy, no transcode, no
                // quality change. Overwrites a stale earlier copy.
                File.Copy(primaryPath, targetPath, overwrite: true);
                return new(true, targetPath, null);
            }

            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null)
                return new(false, targetPath, "ffmpeg not found on this host");

            var args = BuildArgs(primaryPath, targetPath, archiveFormat);
            var (exit, _, stderr) = Run(ffmpeg, args, expectedDuration is { } d ? (int)d.TotalMilliseconds + 30000 : 120000);

            if (exit != 0)
            {
                TryDelete(targetPath);
                return new(false, targetPath, $"ffmpeg exited {exit}: {Truncate(stderr, 300)}");
            }

            // The file must exist and be parseable, exactly like the
            // primary's integrity gate. A zero-byte or corrupt output
            // is removed so the next run starts clean.
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                return new(false, targetPath, "ffmpeg produced no output");

            var integrity = FileIntegrity.Check(targetPath, expectedDuration);
            if (!integrity.Ok)
            {
                TryDelete(targetPath);
                return new(false, targetPath, $"archive transcode looks corrupt: {integrity.Reason}");
            }

            // Metadata handover: ffmpeg keeps Vorbis and ID3 tags in most
            // conversions, but a container switch can drop them. When the
            // MELODY_ID survived, everything meaningful did (reconciliation
            // joins on it); when it did not, write the full tag set the
            // same way the primary path does.
            if (!string.IsNullOrEmpty(melodyId)
                && TaglibHelper.ReadMelodyId(targetPath) != melodyId)
            {
                TaglibHelper.WriteTags(targetPath, title, artist, album, track: trackNumber);
                TaglibHelper.WriteMelodyId(targetPath, melodyId);
            }

            return new(true, targetPath, null);
        }
        catch (Exception ex)
        {
            TryDelete(targetPath);
            return new(false, targetPath, Truncate(ex.Message, 300));
        }
    }

    /// <summary>
    /// ffmpeg arguments per container. FLAC and Opus carry cover art and
    /// text in Vorbis comments; -map_metadata 0 keeps them across the
    /// transcode. mp3 keeps ID3v2 the same way.
    /// </summary>
    private static string BuildArgs(string primaryPath, string targetPath, string archiveFormat)
    {
        var codec = archiveFormat.Trim().ToLowerInvariant() switch
        {
            "opus" => "-c:a libopus -b:a 128k",
            "flac" => "-c:a flac -compression_level 5",
            "mp3" => "-c:a libmp3lame -b:a 256k",
            "aac" => "-c:a aac -b:a 192k",
            _ => "-c:a copy",
        };
        // Opus lives in an ogg container, the rest map to their own
        // extension; -map_metadata 0 is what preserves the tags.
        return $"-nostdin -y -i {primaryPath} -map_metadata 0 -vn {codec} {targetPath}";
    }

    /// <summary>
    /// Same filename as the primary, extension of the archive format
    /// (Auto keeps the primary's). Deterministic: a re-run replaces.
    /// </summary>
    private static string BuildTargetName(string primaryPath, string? archiveFormat)
    {
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        var format = archiveFormat?.Trim().ToLowerInvariant();
        var extension = format is null || format == "auto"
            ? Path.GetExtension(primaryPath)
            : format == "aac" ? ".m4a" : "." + format;
        return stem + extension;
    }

    /// <summary>ffmpeg path on this host, mirroring FindFfprobe's discovery.</summary>
    public static string? FindFfmpeg() => SpectrumAnalyzer.FindBinary("ffmpeg");

    private static (int exit, string stdout, string stderr) Run(string ffmpeg, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg failed to start");
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(timeoutMs))
        {
            TryKill(proc);
            return (-1, "", "ffmpeg timed out");
        }
        return (proc.ExitCode, "", stderr);
    }

    private static void TryKill(Process proc)
    {
        try { proc.Kill(); } catch { /* already gone */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
