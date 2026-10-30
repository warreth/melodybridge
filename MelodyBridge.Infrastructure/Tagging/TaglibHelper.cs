using System.Text.RegularExpressions;
using TagLib;

namespace MelodyBridge.Infrastructure.Tagging;

/// <summary>The tag facts one file carries, for adoption and duplicate matching.</summary>
public readonly record struct AudioFileIdentity(
    string? MelodyId,
    string? Title,
    string? Artist,
    TimeSpan? Duration,
    string? Album);

public static class TaglibHelper
{
    // Marker format used in the Comment fallback: "MELODY_ID={id}" on its own line.
    private const string MelodyIdField = "MELODY_ID";

    // Matches "MELODY_ID=value" when it appears as the start of a line
    // (multiline), so a marker embedded in a longer comment is found.
    private static readonly Regex CommentMarkerRegex =
        new("^MELODY_ID=([^\\s]+)", RegexOptions.Multiline | RegexOptions.Compiled);

    // Write a MELODY_ID into an ID3v2 TXXX frame (UserTextInformationFrame) for
    // ID3 formats, a Xiph comment field for Ogg/FLAC/Opus, and an idempotent
    // Comment marker fallback for MP4 and anything else.
    public static void WriteMelodyId(string filePath, string melodyId)
    {
        try
        {
            var file = TagLib.File.Create(filePath);

            // Path 1: Xiph comment (Ogg Vorbis, Opus, FLAC). Checked first because
            // GetTag(Id3v2, create:true) would fabricate an ID3v2 tag inside FLAC,
            // hijacking the native storage for those formats.
            try
            {
                var xiph = file.GetTag(TagTypes.Xiph, true) as TagLib.Ogg.XiphComment;
                if (xiph != null)
                {
                    xiph.SetField(MelodyIdField, melodyId);
                    file.Save();
                    return;
                }
            }
            catch { }

            // Path 2: ID3v2 TXXX (mp3, and anything else carrying an ID3v2 tag).
            try
            {
                var id3v2 = file.GetTag(TagTypes.Id3v2, true) as TagLib.Id3v2.Tag;
                if (id3v2 != null)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, MelodyIdField, true);
                    frame.Text = new[] { melodyId };
                    file.Save();
                    return;
                }
            }
            catch { }

            // Path 3 (fallback, MP4 and others): comment marker. Replace the whole
            // comment with a single marker so rewriting never duplicates or grows.
            var comment = file.Tag.Comment ?? string.Empty;
            var marker = MelodyIdField + "=" + melodyId;
            file.Tag.Comment = CommentMarkerRegex.IsMatch(comment)
                ? CommentMarkerRegex.Replace(comment, marker)
                : marker;
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
        uint? year = null, byte[]? coverArt = null, string? lyrics = null)
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
            if (!string.IsNullOrWhiteSpace(lyrics))
                file.Tag.Lyrics = lyrics;
            file.Save();
        }
        catch
        {
            // Non-fatal: tagging failure
        }
    }

    /// <summary>
    /// True when the file already carries embedded cover art, so a fetched
    /// cover never overwrites art a plugin already wrote.
    /// </summary>
    public static bool HasPictures(string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            return file.Tag.Pictures.Length > 0;
        }
        catch
        {
            // Unreadable file: report "no art" is wrong, but the caller only
            // uses this to decide whether to embed; embedding is harmless.
            return false;
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

    /// <summary>
    /// The tag facts an adoption pass needs, read in one open: the stored
    /// MELODY_ID, the title, the first performer and the audio duration.
    /// A file that cannot be read comes back as an empty identity.
    /// </summary>
    public static AudioFileIdentity ReadIdentity(string filePath)
    {
        try
        {
            var file = TagLib.File.Create(filePath);
            return new AudioFileIdentity(
                ReadMelodyId(file),
                file.Tag.Title,
                file.Tag.FirstPerformer,
                file.Properties?.Duration,
                file.Tag.Album);
        }
        catch
        {
            return default;
        }
    }

    public static string? ReadMelodyId(string filePath)
    {
        try
        {
            return ReadMelodyId(TagLib.File.Create(filePath));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadMelodyId(TagLib.File file)
    {
        // ID3v2 TXXX
        try
        {
            var id3v2 = file.GetTag(TagTypes.Id3v2) as TagLib.Id3v2.Tag;
            if (id3v2 != null)
            {
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, MelodyIdField, false);
                if (frame != null && frame.Text?.Length > 0)
                    return frame.Text[0];
            }
        }
        catch { }

        // Xiph comment field (Ogg Vorbis/Opus, FLAC)
        try
        {
            var xiph = file.GetTag(TagTypes.Xiph) as TagLib.Ogg.XiphComment;
            var field = xiph?.GetFirstField(MelodyIdField);
            if (!string.IsNullOrEmpty(field))
                return field;
        }
        catch { }

        // Comment marker
        var comment = file.Tag.Comment ?? string.Empty;
        var match = CommentMarkerRegex.Match(comment);
        return match.Success ? match.Groups[1].Value : null;
    }

}
