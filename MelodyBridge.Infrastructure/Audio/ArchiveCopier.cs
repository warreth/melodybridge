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

        // Copying a file onto itself destroys it: File.Copy fails, the
        // failure cleanup below would delete the target, and the target
        // is the primary download. Refuse before touching anything and
        // let the track warning name the fix.
        if (PathsEqual(primaryPath, targetPath))
        {
            return new(false, targetPath,
                "archive directory is the same as the download directory; set a different archive folder");
        }

        try
        {
            Directory.CreateDirectory(archiveDirectory);

            // Replace by name, never through an existing inode: an archive
            // copy can be a hardlink to another playlist's archive, so
            // truncating the target in place (ffmpeg or File.Copy with
            // overwrite) would corrupt the twin when this attempt fails.
            // Unlinking first leaves every other name on the old bytes.
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Delete(targetPath);
                }
                catch (Exception ex)
                {
                    return new(false, targetPath,
                        $"could not replace the existing archive file: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(archiveFormat)
                || archiveFormat.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // Auto archive: byte-for-byte copy, no transcode, no
                // quality change. The old name is already gone.
                File.Copy(primaryPath, targetPath, overwrite: false);
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

            // The archive mirrors the primary, so the primary's own
            // measured length is the yardstick, never the playlist's
            // expectation: a flagged wrong-length file must still get its
            // archive copy, while a silently truncated transcode is
            // caught. The tight window allows codec padding; only
            // unreadable output counts as corrupt.
            var primaryDuration = FileIntegrity.MeasureDuration(primaryPath);
            var tolerance = primaryDuration is { } source
                ? TimeSpan.FromSeconds(Math.Max(2, source.TotalSeconds * 0.02))
                : (TimeSpan?)null;
            var integrity = FileIntegrity.Check(targetPath, primaryDuration, tolerance);
            // A missing ffprobe is not proof that the copy is broken: keep it.
            if (!integrity.Ok && !integrity.ToolMissing)
            {
                TryDelete(targetPath);
                return new(false, targetPath,
                    integrity.Issue == IntegrityIssue.Corrupt
                        ? $"archive transcode looks corrupt: {integrity.Reason}"
                        : $"archive transcode does not match the source: {integrity.Reason}");
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
    /// transcode. mp3 keeps ID3v2 the same way. Arguments travel as a
    /// list, so paths with spaces stay single arguments.
    /// </summary>
    private static List<string> BuildArgs(string primaryPath, string targetPath, string archiveFormat)
    {
        var args = new List<string>
        {
            "-nostdin", "-y", "-i", primaryPath, "-map_metadata", "0", "-vn",
        };
        switch (archiveFormat.Trim().ToLowerInvariant())
        {
            case "opus": args.AddRange(new[] { "-c:a", "libopus", "-b:a", "128k" }); break;
            case "flac": args.AddRange(new[] { "-c:a", "flac", "-compression_level", "5" }); break;
            case "mp3": args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", "256k" }); break;
            case "aac": args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" }); break;
            default: args.AddRange(new[] { "-c:a", "copy" }); break;
        }
        args.Add(targetPath);
        return args;
    }

    /// <summary>
    /// Same filename as the primary, extension of the archive format
    /// (Auto keeps the primary's). Deterministic: a re-run replaces.
    /// </summary>
    private static string BuildTargetName(string primaryPath, string? archiveFormat)
        => Path.GetFileNameWithoutExtension(primaryPath) + TargetExtension(primaryPath, archiveFormat);

    /// <summary>
    /// The extension an archive copy lands with: the format's (AAC lands
    /// as .m4a), or the primary's own for Auto. Callers outside the copier
    /// use it to tell whether an existing archive file already matches
    /// this playlist's configured format.
    /// </summary>
    public static string TargetExtension(string primaryPath, string? archiveFormat)
    {
        var format = archiveFormat?.Trim().ToLowerInvariant();
        return format is null || format == "auto"
            ? Path.GetExtension(primaryPath)
            : format == "aac" ? ".m4a" : "." + format;
    }

    /// <summary>ffmpeg path on this host, mirroring FindFfprobe's discovery.</summary>
    public static string? FindFfmpeg() => SpectrumAnalyzer.FindBinary("ffmpeg");

    private static (int exit, string stdout, string stderr) Run(
        string ffmpeg, IReadOnlyList<string> args, int timeoutMs)
    {
        var result = ToolProcessRunner.Run(ffmpeg, args, timeoutMs);
        return result.TimedOut
            ? (-1, string.Empty, "ffmpeg timed out")
            : (result.ExitCode, result.Stdout, result.Stderr);
    }

    /// <summary>Full-path comparison; case-insensitive on Windows/macOS-style filesystems.</summary>
    private static bool PathsEqual(string first, string second)
    {
        var a = Path.GetFullPath(first);
        var b = Path.GetFullPath(second);
        return string.Equals(a, b,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
