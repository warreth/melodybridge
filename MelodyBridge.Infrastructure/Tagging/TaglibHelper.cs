using TagLib;

namespace MelodyBridge.Infrastructure.Tagging;

public static class TaglibHelper
{
    // Write a MELODY_ID into an ID3v2 TXXX frame (UserTextInformationFrame) when possible,
    // and fallback to Comment tag for other formats.
    public static void WriteMelodyId(string filePath, string melodyId)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            // Try ID3v2 TXXX
            try
            {
                var id3v2 = file.GetTag(TagTypes.Id3v2, true) as TagLib.Id3v2.Tag;
                if (id3v2 != null)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, "MELODY_ID", true);
                    frame.Text = new[] { melodyId };
                    file.Save();
                    return;
                }
            }
            catch { }

            // Fallback: append to comment
            file.Tag.Comment = (file.Tag.Comment ?? string.Empty) + " MELODY_ID=" + melodyId;
            file.Save();
        }
        catch
        {
            // Non-fatal: tagging failure
        }
    }

    /// <summary>
    /// Writes standard tags. Missing values are left untouched.
    /// Non-fatal: failures are swallowed (tagging must never break a download).
    /// </summary>
    public static void WriteTags(
        string filePath, string? title = null, string? artist = null,
        string? album = null, string? albumArtist = null, uint? track = null,
        uint? year = null, byte[]? coverArt = null)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            if (!string.IsNullOrWhiteSpace(title)) file.Tag.Title = title;
            if (!string.IsNullOrWhiteSpace(artist)) file.Tag.Performers = new[] { artist };
            if (!string.IsNullOrWhiteSpace(album)) file.Tag.Album = album;
            if (!string.IsNullOrWhiteSpace(albumArtist)) file.Tag.AlbumArtists = new[] { albumArtist };
            if (track is > 0) file.Tag.Track = track.Value;
            if (year is > 0) file.Tag.Year = year.Value;
            if (coverArt is { Length: > 0 })
                file.Tag.Pictures = new[] { new TagLib.Picture(coverArt) };
            file.Save();
        }
        catch
        {
            // Non-fatal: tagging failure
        }
    }

    /// <summary>
    /// True when the file already carries a real title tag (not a filename
    /// stub), so the caller can decide whether to overwrite it.
    /// </summary>
    public static bool HasTitleTag(string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            return !string.IsNullOrWhiteSpace(file.Tag.Title);
        }
        catch
        {
            return false;
        }
    }

    public static string? ReadMelodyId(string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            // ID3v2
            try
            {
                var id3v2 = file.GetTag(TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                if (id3v2 != null)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, "MELODY_ID", false);
                    if (frame != null && frame.Text?.Length > 0)
                        return frame.Text[0];
                }
            }
            catch { }

            // Fallback: comment
            var comment = file.Tag.Comment ?? string.Empty;
            var marker = "MELODY_ID=";
            var idx = comment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var after = comment.Substring(idx + marker.Length).Trim();
                var parts = after.Split(new[] { ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 ? parts[0] : after;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
