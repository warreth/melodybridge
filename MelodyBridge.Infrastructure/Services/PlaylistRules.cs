using System.Text;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The pure rules of the playlist domain: quality parsing, sync-mode
/// parsing, the quality wording, the post-download warning, filename
/// slugging and the source-track to row mapping. No database, no network,
/// no state, so every caller (the store, the snapshots service and the
/// download pipeline) shares one implementation.
/// </summary>
internal static class PlaylistRules
{
    /// <summary>
    /// Maps a playlist's PreferredFormat to the waterfall quality.
    /// "preset:saver" | "preset:high" | "preset:lossless" | "auto" name
    /// the shared presets; a raw container ("mp3", "flac", "opus", "aac")
    /// optionally suffixed with a bitrate ("mp3:192" cap, "mp3:192-320"
    /// band, open bands "-320" / "192-") is the advanced path.
    /// </summary>
    internal static DownloadQuality ParseQuality(string? preferredFormat)
    {
        if (QualityPresets.TryParse(preferredFormat, out var preset))
            return QualityPresets.ToQuality(preset);

        return ParseRawQuality(preferredFormat);
    }

    internal static DownloadQuality ParseRawQuality(string? preferredFormat)
    {
        preferredFormat ??= "auto";
        var parts = preferredFormat.Split(':', 2);
        var format = parts[0].ToLowerInvariant() switch
        {
            "mp3" => AudioFormat.Mp3,
            "flac" => AudioFormat.Flac,
            "opus" => AudioFormat.Opus,
            "aac" => AudioFormat.Aac,
            _ => AudioFormat.Auto,
        };

        if (parts.Length < 2)
            return new DownloadQuality(format);

        // "192" is a cap; "min-max", "-max" and "min-" are bands.
        var range = parts[1].Split('-', 2);
        var min = range.Length == 2 && int.TryParse(range[0], out var lo) && lo > 0 ? lo : (int?)null;
        var max = int.TryParse(range.Length == 2 ? range[1] : range[0], out var hi) && hi > 0 ? hi : (int?)null;
        return new DownloadQuality(format, min, max);
    }

    /// <summary>Human form of a quality request for warnings and logs.</summary>
    internal static string DescribeQuality(DownloadQuality q)
    {
        var container = DedupQuality.RequestedContainer(q);
        var band = (q.MinKbps, q.MaxKbps) switch
        {
            (null, null) => "any bitrate",
            (null, { } max) => $"up to {max} kbps",
            ({ } min, null) => $"{min}+ kbps",
            ({ } min, { } max) => $"{min}-{max} kbps",
        };
        return container is null ? band : $"{container}, {band}";
    }


    /// <summary>Filename-stable slug: lowercase letters/digits, rest to '-'.</summary>
    internal static string Slugify(string name)
    {
        var slug = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        return slug.ToString().Trim('-') is { Length: > 0 } s ? s : "import";
    }

    /// <summary>
    /// Folder name for a playlist's own directory under the music path.
    /// Readable (keeps case and spaces), only characters a filesystem
    /// rejects are replaced, and Windows' reserved device names get a
    /// prefix so a playlist called "aux" cannot break a library scan.
    /// </summary>
    internal static string FolderNameFor(string? playlistName)
    {
        var raw = playlistName?.Trim() ?? "";
        if (raw.Length == 0) return "playlist";

        var invalid = Path.GetInvalidFileNameChars();
        var name = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            name.Append(invalid.Contains(ch) ? '_' : ch);
        var safe = name.ToString().Trim().Trim(' ', '.');
        if (safe.Length == 0) return "playlist";
        if (safe.Length > 100) safe = safe[..100].TrimEnd(' ', '.');
        return ReservedDeviceNames.Contains(safe) ? "_" + safe : safe;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The folder a playlist's downloads actually land in. An explicit
    /// folder (anything other than the app-wide music path) is used as it
    /// is; the app-wide default counts as "no choice" and gets a
    /// subfolder named after the playlist, so every playlist owns its
    /// folder and its dedup links. Empty when nothing is configured.
    /// </summary>
    internal static string ResolvePlaylistFolder(string? targetDirectory, string defaultMusicPath, string? playlistName)
        => ResolveWithPlaylistFolder(targetDirectory, defaultMusicPath, playlistName);

    /// <summary>
    /// The playlist's effective archive folder, same rule as the download
    /// folder: an explicit folder wins, the app-wide archive path gets a
    /// subfolder per playlist. Empty when no archive path is configured.
    /// </summary>
    internal static string ResolveArchiveFolder(string? archiveDirectory, string defaultArchivePath, string? playlistName)
        => ResolveWithPlaylistFolder(archiveDirectory, defaultArchivePath, playlistName);

    private static string ResolveWithPlaylistFolder(string? explicitFolder, string defaultRoot, string? playlistName)
    {
        if (!string.IsNullOrWhiteSpace(explicitFolder) && !PathEquals(explicitFolder, defaultRoot))
            return explicitFolder.Trim();
        var root = !string.IsNullOrWhiteSpace(explicitFolder) ? explicitFolder.Trim()
            : defaultRoot?.Trim() ?? "";
        if (root.Length == 0) return string.Empty;
        return Path.Combine(root, FolderNameFor(playlistName));
    }

    /// <summary>Path equality ignoring a trailing separator; case-insensitive on Windows/macOS.</summary>
    private static bool PathEquals(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        var a = first.Trim().TrimEnd('/', '\\');
        var b = second.Trim().TrimEnd('/', '\\');
        return string.Equals(a, b,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    /// <summary>Base format values shown in the UI dropdown.</summary>
    public static readonly string[] FormatOptions = { "auto", "mp3", "flac", "opus", "aac" };

    /// <summary>Validates a PreferredFormat string ("auto", "preset:saver", "mp3:192", legacy "mp3:192-320").</summary>
    public static bool IsValidFormat(string value)
    {
        if (QualityPresets.IsPreset(value)) return true;
        var parts = value.Split(':', 2);
        if (!FormatOptions.Contains(parts[0].ToLowerInvariant())) return false;
        if (parts.Length == 1) return true;

        var range = parts[1].Split('-', 2);
        foreach (var side in range)
            if (side.Length > 0 && (!int.TryParse(side, out var n) || n <= 0))
                return false;
        return true;
    }

    internal static PlaylistSyncMode ParseSyncMode(string? raw)
        => Enum.TryParse<PlaylistSyncMode>(raw, true, out var mode) ? mode : PlaylistSyncMode.Additive;

    /// <summary>
    /// Which of the tags this action fills are not on the file yet. An
    /// unreadable file throws and the caller counts it as failed.
    /// </summary>
    internal static (bool Album, bool AlbumArtist, bool Lyrics, bool Cover) MissingTags(string path)
    {
        var file = TagLib.File.Create(path);
        return (string.IsNullOrWhiteSpace(file.Tag.Album),
            file.Tag.AlbumArtists.Length == 0,
            string.IsNullOrWhiteSpace(file.Tag.Lyrics),
            file.Tag.Pictures.Length == 0);
    }

    internal static TrackEntity MapTrack(Track track, Platform platform, string playlistId, int position)
    {
        var externalId = track.PlatformSongID?.ID ?? track.SongID?.ID;
        var songId = track.PlatformSongID ?? track.SongID;
        return new TrackEntity
        {
            // Deterministic per source track: wiping the database and
            // re-adding the same playlist yields the same ids, so the
            // files already on disk keep matching and nothing re-downloads.
            // No native id: import rows hash their metadata (csv:), any
            // other row falls back to the mbh: hash.
            MelodyId = songId is not null
                ? MelodyIds.For(songId)
                : platform == Platform.Unknown
                    ? MelodyIds.ForCsv(track.Artist, track.Title, track.Duration)
                    : MelodyIds.ForUnknown(track.Artist, track.Title, track.Duration),
            ExternalId = externalId,
            ExternalPlatform = platform.ToString(),
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            AlbumArtist = track.AlbumArtist,
            CoverUrl = track.CoverUrl,
            Lyrics = track.Lyrics,
            Isrc = track.Isrc,
            DurationMs = (long?)track.Duration?.TotalMilliseconds,
            MediaType = track.MediaType.ToString(),
            SourceUrl = track.CurrentTrackLocation?.Path,
            Platform = platform.ToString(),
            Position = position,
            DownloadStatus = "pending",
            PlaylistSnapshotId = null,
            CurrentPath = null,
        };
    }
}
