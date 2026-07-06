using System.Diagnostics;
using TagLib;

namespace MelodyBridge.Infrastructure.Downloaders;

using MelodyBridge.Core;

public class YouTubeDownloader : IAsyncDownloader
{
    /// <summary>
    /// Download audio using yt-dlp (yt-dlp or yt-dlp present on PATH) and write a MELODY_ID tag.
    /// This is a minimal placeholder implementation that shells out to an external binary.
    /// </summary>
    public async Task<string> DownloadAsync(string videoUrl, string outputDirectory, string melodyId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        // output template: {title}.%(ext)s
        var outputTemplate = Path.Combine(outputDirectory, "%(title)s.%(ext)s");

        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            ArgumentList = { "-x", "--audio-format", "mp3", "-o", outputTemplate, videoUrl },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        if (proc == null) throw new InvalidOperationException("Failed to start yt-dlp");

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"yt-dlp failed: {err}");
        }

        // Find latest file in outputDirectory
        var downloaded = Directory.EnumerateFiles(outputDirectory)
            .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();

        if (downloaded == null) throw new FileNotFoundException("No downloaded file found");

        // Write MELODY_ID using TaglibHelper
        try
        {
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(downloaded, melodyId);
        }
        catch
        {
            // tag write failure is non-fatal for placeholder
        }

        return downloaded;
    }

    public bool CanHandle(string sourceIdentifier)
    {
        return sourceIdentifier?.Contains("youtube", StringComparison.OrdinalIgnoreCase) == true || sourceIdentifier?.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) == true;
    }

    public string Name => "yt-dlp";
}
