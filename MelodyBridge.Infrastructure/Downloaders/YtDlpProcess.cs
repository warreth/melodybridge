using System.Diagnostics;
using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Shared yt-dlp process plumbing: binary resolution on PATH and a
/// cancellable, timeout-guarded argument-list runner used by all
/// yt-dlp-backed downloader plugins.
/// </summary>
internal static class YtDlpProcess
{
    private static readonly Lazy<string?> Binary = new(ResolveBinary);

    /// <summary>Resolved yt-dlp binary path, or null when not installed.</summary>
    public static string? BinaryPath => Binary.Value;

    /// <summary>
    /// Download arguments shared by the yt-dlp-backed plugins. Always
    /// audio-only: yt-dlp's default format chain is
    /// bestvideo*+bestaudio, which downloads full video files (tens of
    /// megabytes) for a music library that only needs the audio stream
    /// (a few megabytes). Explicit formats add the transcode flags.
    /// </summary>
    public static List<string> BuildDownloadArgs(string template, string sourceUrl, DownloadQuality quality)
    {
        var args = new List<string>
        {
            "-f", "bestaudio/best",
            "-o", template,
            sourceUrl,
            "--print-json",
            "--no-simulate",
            // A watch URL carrying a list parameter must download the
            // track, never the whole playlist.
            "--no-playlist",
            "--socket-timeout", "20",
            "--retries", "3",
            "--fragment-retries", "3",
        };
        if (quality.NeedsTranscode)
        {
            args.InsertRange(0, new[]
            {
                "-x",
                "--audio-format", AudioFormatArg(quality.Format),
                "--audio-quality", quality.YtDlpAudioQuality,
                "--embed-metadata",
            });
        }
        return args;
    }

    /// <summary>yt-dlp's --audio-format name for the requested container.</summary>
    private static string AudioFormatArg(AudioFormat format) => format switch
    {
        AudioFormat.Aac => "m4a", // yt-dlp names the AAC container m4a
        AudioFormat.Opus => "opus",
        AudioFormat.Flac => "flac",
        _ => "mp3",
    };

    /// <summary>
    /// Reads the exact audio facts of one candidate without downloading:
    /// one simulate-only extraction with the same format selection a real
    /// download would use, then parse the selected format.
    /// </summary>
    public static async Task<CandidateAudioQuality?> ProbeAudioQualityAsync(
        string sourceUrl, CancellationToken ct = default)
    {
        if (BinaryPath is null || string.IsNullOrWhiteSpace(sourceUrl)) return null;
        try
        {
            var (exit, stdout, _) = await RunAsync(new[]
            {
                "-f", "bestaudio/best",
                "--simulate",
                "--no-download",
                "--dump-single-json",
                "--no-warnings",
                "--no-playlist",
                "--socket-timeout", "20",
                sourceUrl,
            }, TimeSpan.FromSeconds(30), ct);
            if (exit != 0) return null;
            return ParseAudioQualityJson(stdout);
        }
        catch
        {
            // A probe is best effort: an unknown candidate shows no facts.
            return null;
        }
    }

    /// <summary>
    /// Reads the artist, title and length of one link without downloading
    /// it: one simulate-only extraction. The manual download page uses
    /// this to name and tag a pasted link; null means the link could not
    /// be inspected, not that it cannot be downloaded.
    /// </summary>
    public static async Task<UrlTrackMetadata?> ResolveTrackMetadataAsync(
        string url, CancellationToken ct = default)
    {
        if (BinaryPath is null || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var (exit, stdout, _) = await RunAsync(new[]
            {
                "--simulate",
                "--no-download",
                "--dump-single-json",
                "--no-warnings",
                "--no-playlist",
                "--socket-timeout", "20",
                url,
            }, TimeSpan.FromSeconds(30), ct);
            if (exit != 0) return null;
            return ParseTrackMetadataJson(stdout);
        }
        catch
        {
            // Naming metadata is a convenience; the download can still run.
            return null;
        }
    }

    /// <summary>
    /// Reads the first entry of a yt-dlp JSON document. A title that still
    /// carries the usual "Artist - Title" video shape gives up its artist
    /// when no artist field exists.
    /// </summary>
    internal static UrlTrackMetadata? ParseTrackMetadataJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array
                && entries.GetArrayLength() > 0)
            {
                root = entries[0];
            }

            var title = JsonString(root, "track") ?? JsonString(root, "title");
            var artist = JsonString(root, "artist")
                ?? JsonString(root, "creator")
                ?? JsonString(root, "uploader")
                ?? JsonString(root, "channel");

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                return null;

            if (string.IsNullOrWhiteSpace(artist) && title is { Length: > 0 })
            {
                var separator = title.IndexOf(" - ", StringComparison.Ordinal);
                if (separator > 0 && separator < title.Length - 3)
                {
                    artist = title[..separator].Trim();
                    title = title[(separator + 3)..].Trim();
                }
            }

            var duration = JsonInt(root, "duration");
            return new UrlTrackMetadata(
                string.IsNullOrWhiteSpace(artist) ? null : CleanChannelName(artist),
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Drops the channel decorations YouTube puts around an artist.</summary>
    private static string CleanChannelName(string artist)
    {
        var trimmed = artist.Trim();
        foreach (var suffix in new[] { " - Topic", "VEVO", " Official", "Official" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed[..^suffix.Length].Trim();
                if (stripped.Length > 0) trimmed = stripped;
            }
        }
        return trimmed;
    }

    /// <summary>
    /// Parses yt-dlp's simulate JSON into the selected format's facts. When
    /// the root is a playlist (a search URL), the first entry is read. A
    /// null result means the JSON carried no usable audio fields.
    /// </summary>
    internal static CandidateAudioQuality? ParseAudioQualityJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("entries", out var entries)
                && entries.ValueKind == JsonValueKind.Array
                && entries.GetArrayLength() > 0)
            {
                root = entries[0];
            }

            var codec = JsonString(root, "acodec");
            if (string.IsNullOrWhiteSpace(codec) || codec == "none") return null;

            var bitrate = JsonInt(root, "abr") ?? JsonInt(root, "tbr");
            var sampleRate = JsonInt(root, "asr");
            var note = JsonString(root, "format_note");

            return new CandidateAudioQuality(
                Codec: codec,
                BitrateKbps: bitrate,
                SampleRateHz: sampleRate,
                BitDepth: null,
                Lossless: IsLosslessCodec(codec),
                Note: string.IsNullOrWhiteSpace(note) ? null : note);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True for the codecs yt-dlp can hand back losslessly.</summary>
    internal static bool IsLosslessCodec(string? codec)
        => codec?.ToLowerInvariant() is { } c
           && (c.StartsWith("flac", StringComparison.Ordinal)
               || c.StartsWith("alac", StringComparison.Ordinal)
               || c.StartsWith("pcm_", StringComparison.Ordinal)
               || c == "wav");

    private static string? JsonString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number) && number > 0
            ? (int)Math.Round(number)
            : null;

    /// <summary>
    /// Runs a download with one retry on a transient failure (YouTube 403s
    /// and timeouts happen under parallel downloads; a fresh extraction
    /// usually hands out new media URLs). Caller cancellation and real
    /// errors propagate immediately.
    /// </summary>
    public static async Task<(int exit, string stdout, string stderr)> RunDownloadAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var first = await RunAsync(arguments, timeout, ct);
        if (first.exit == 0 || !IsTransientFailure(first.stderr))
            return first;

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        return await RunAsync(arguments, timeout, ct);
    }

    /// <summary>Media-URL 403/429/5xx and connection timeouts are worth a second try.</summary>
    internal static bool IsTransientFailure(string stderr)
        => stderr.Contains("HTTP Error 403", StringComparison.Ordinal)
        || stderr.Contains("HTTP Error 429", StringComparison.Ordinal)
        || stderr.Contains("HTTP Error 5", StringComparison.Ordinal)
        || stderr.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Temporary failure", StringComparison.OrdinalIgnoreCase);

    public static async Task<(int exit, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var path = BinaryPath
            ?? throw new InvalidOperationException("yt-dlp binary not found");

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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start yt-dlp");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout, not the caller's cancellation.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"yt-dlp did not finish within {timeout.TotalSeconds}s");
        }
    }

    private static string? ResolveBinary()
    {
        foreach (var name in new[] { "yt-dlp", "yt-dlp_linux", "youtube-dl" })
        {
            var found = FindOnPath(name);
            if (found is not null) return found;
        }
        return null;
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
