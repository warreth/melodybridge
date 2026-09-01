using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Reads real audio facts (sample rate Hz, file size, container) from a
/// downloaded file and fills them into the track row. Bitrate is measured
/// separately with ffprobe (BitrateProbe) and is left untouched here.
/// Kept separate from the download path so the scanner can reuse it.
/// </summary>
public static class AudioProbe
{
    public static void Fill(TrackEntity track, string path)
    {
        var info = new FileInfo(path);
        track.FileSizeBytes = info.Exists ? info.Length : null;

        try
        {
            var tf = TagLib.File.Create(path);
            if (tf.Properties?.AudioSampleRate is > 0)
                track.SampleRateHz = tf.Properties.AudioSampleRate;
            var container = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (container.Length > 0)
                track.MediaType = container;
        }
        catch
        {
            // Unreadable tags: size is still a fact, sample rate stays null.
        }
    }
}
