using System.Security.Cryptography;
using System.Text;

namespace MelodyBridge.Core;

/// <summary>
/// Deterministic MELODY_ID values, one per source track.
///
/// The MELODY_ID tag inside a downloaded file is the source of truth for
/// "which track is this file". The database is only a cache of it: wipe the
/// database, re-add the same playlist, and every track row must get the same
/// id again so the files already on disk still match and nothing
/// re-downloads. The old random "mb-{guid}" ids broke that promise, because
/// a rebuilt database never guessed the same guid twice.
///
/// The id is derived only from values the source platform reports, so the
/// same source item always maps to the same string on every machine:
///   spotify:{spotifyTrackId}
///   yt:{youtubeVideoId}
///   ia:{archiveOrgIdentifier}
///   csv:{hash}  for import rows with no native id
///   mbh:{hash}  when no id is known at all
/// The platform id itself is kept verbatim: YouTube ids are case-sensitive
/// and Spotify ids are base62, so only the prefix is ever lowercased.
/// </summary>
public static class MelodyIds
{
    /// <summary>Deterministic id for a track that carries a platform id.</summary>
    public static string For(SongID id) => For(id.Platform.ToString(), id.ID);

    /// <summary>
    /// Deterministic id for a platform id given as raw strings: the form
    /// the database stores (ExternalPlatform + ExternalId), used by the
    /// schema patcher when it backfills old rows.
    /// </summary>
    public static string For(string? platform, string id) => $"{Prefix(platform)}:{id}";

    /// <summary>
    /// Deterministic id for an import row with no native id: Exportify CSV
    /// rows without a Track URI and manual entries.
    /// </summary>
    public static string ForCsv(string? artist, string? title, TimeSpan? duration)
        => $"csv:{Hash(artist, title, duration)}";

    /// <summary>
    /// Deterministic id when no platform id is known at all; hashes the
    /// same artist, title and duration as the csv form.
    /// </summary>
    public static string ForUnknown(string? artist, string? title, TimeSpan? duration)
        => $"mbh:{Hash(artist, title, duration)}";

    /// <summary>
    /// Short lowercase prefix per platform. Known names get their fixed
    /// short form; anything else passes through lowercased so future
    /// platforms stay deterministic without touching this file.
    /// </summary>
    private static string Prefix(string? platform)
    {
        var name = (platform ?? "").Trim().ToLowerInvariant();
        return name switch
        {
            "spotify" => "spotify",
            "youtubemusic" or "youtube" => "yt",
            "ia" or "internetarchive" or "archiveorg" => "ia",
            "" => "unknown",
            _ => name,
        };
    }

    /// <summary>
    /// First 16 hex chars of SHA-256 over "artist|title|durationSeconds",
    /// each part trimmed and lowercased. Enough to tell two songs apart
    /// and stable forever.
    /// </summary>
    private static string Hash(string? artist, string? title, TimeSpan? duration)
    {
        var normalized = $"{Normalize(artist)}|{Normalize(title)}|{(long?)duration?.TotalSeconds}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();
}
