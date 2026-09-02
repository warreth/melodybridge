using System.Text.Json;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>One playlist parsed from a user-provided export file.</summary>
public sealed record ImportedPlaylist(string Name, string? Owner, IReadOnlyList<Track> Tracks);

/// <summary>A parsed export file: its flavor plus the playlists inside.</summary>
public sealed record ImportedFile(string Kind, IReadOnlyList<ImportedPlaylist> Playlists);

/// <summary>
/// Parses the two file flavors a Spotify user without API access can
/// produce: an Exportify CSV (exportify.net) and Spotify's official
/// privacy data export (YourLibrary.json / Playlist1.json). Pure
/// functions; never throws on malformed input — a bad row is skipped,
/// an unrecognizable file returns null.
/// </summary>
public static class PlaylistFileImporter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Detects and parses the file flavor from its content.</summary>
    public static ImportedFile? Parse(string fileName, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var trimmed = content.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            if (content.Contains("\"tracks\"", StringComparison.Ordinal))
                return ParseYourLibrary(content);
            if (content.Contains("\"playlists\"", StringComparison.Ordinal))
                return ParsePlaylists1(content);
            return null;
        }

        if (trimmed.StartsWith("Track URI,", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Track Name,", StringComparison.OrdinalIgnoreCase))
            return ParseExportifyCsv(content);

        return null;
    }

    // ── Spotify privacy export: YourLibrary.json (liked songs) ─────────

    private sealed record LibraryTrack(string? Artist, string? Album, string? Track, string? Uri);

    private static ImportedFile? ParseYourLibrary(string content)
    {
        LibraryTrack[]? rows;
        try { rows = JsonSerializer.Deserialize<LibraryTrack[]>(ExtractArray(content, "tracks"), Json); }
        catch (JsonException) { return null; }
        if (rows is null || rows.Length == 0) return null;

        var tracks = new List<Track>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Track) && string.IsNullOrWhiteSpace(row.Uri)) continue;
            tracks.Add(ToTrack(title: row.Track, artist: row.Artist, album: row.Album,
                uri: row.Uri, durationMs: null, isLiked: true));
        }
        if (tracks.Count == 0) return null;

        return new ImportedFile("yourlibrary",
            [new ImportedPlaylist("Liked songs (Spotify)", "you", tracks)]);
    }

    // ── Spotify privacy export: Playlist1.json (all playlists) ──────────

    private sealed record PlaylistTrack(string? TrackName, string? ArtistName, string? AlbumName, string? TrackUri);
    private sealed record PlaylistItem(PlaylistTrack? Track);
    private sealed record PlaylistDoc(string? Name, List<PlaylistItem>? Items);

    private static ImportedFile? ParsePlaylists1(string content)
    {
        PlaylistDoc[]? docs;
        try { docs = JsonSerializer.Deserialize<PlaylistDoc[]>(ExtractArray(content, "playlists"), Json); }
        catch (JsonException) { return null; }
        if (docs is null || docs.Length == 0) return null;

        var playlists = new List<ImportedPlaylist>();
        foreach (var doc in docs)
        {
            var tracks = new List<Track>();
            foreach (var item in doc.Items ?? [])
            {
                if (item.Track is null) continue; // episodes, audiobooks, local files
                tracks.Add(ToTrack(item.Track.TrackName, item.Track.ArtistName, item.Track.AlbumName,
                    item.Track.TrackUri, durationMs: null, isLiked: false));
            }
            if (tracks.Count > 0)
                playlists.Add(new ImportedPlaylist(doc.Name ?? "Spotify playlist", null, tracks));
        }
        if (playlists.Count == 0) return null;

        return new ImportedFile("playlists1", playlists);
    }

    // ── Exportify CSV ───────────────────────────────────────────────────

    private static ImportedFile? ParseExportifyCsv(string content)
    {
        var rows = SplitCsv(content);
        if (rows.Count < 2) return null;

        // Header names are case-insensitive; the "simple" mode has no Track URI.
        var header = rows[0]
            .Select(h => h.Trim().ToLowerInvariant())
            .ToList();
        int Col(string name) => header.IndexOf(name);

        var uriCol = Col("track uri");
        if (uriCol < 0 && Col("track name") < 0) return null;

        var tracks = new List<Track>();
        foreach (var row in rows.Skip(1))
        {
            string Cell(int i) => i >= 0 && i < row.Count ? row[i] : string.Empty;

            var title = Cell(Col("track name")).Trim();
            var artist = Cell(Col("artist name(s)")).Trim();
            if (title.Length == 0 && artist.Length == 0) continue;

            long? durationMs = long.TryParse(Cell(Col("duration (ms)")), out var ms) && ms > 0 ? ms : null;

            tracks.Add(ToTrack(title,
                artist: JoinArtists(artist),
                album: Cell(Col("album name")).Trim(),
                uri: Cell(uriCol).Trim(),
                durationMs: durationMs,
                isLiked: true));
        }
        if (tracks.Count == 0) return null;

        return new ImportedFile("exportify",
            [new ImportedPlaylist("Spotify export", null, tracks)]);
    }

    /// <summary>Exportify joins multi-artist cells with ';'; tags read better as ', '.</summary>
    private static string? JoinArtists(string cell)
        => cell.Length == 0 ? null
            : string.Join(", ", cell.Split(';', StringSplitOptions.TrimEntries));

    // ── shared mapping ─────────────────────────────────────────────────

    private static Track ToTrack(string? title, string? artist, string? album,
        string? uri, long? durationMs, bool isLiked)
    {
        // "spotify:track:ID" → the bare id; anything else is not usable.
        var id = uri is not null && uri.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? uri["spotify:track:".Length..]
            : null;
        if (string.IsNullOrEmpty(id)) id = null;

        return new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            Duration = durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
            SongID = id is null ? null : new SongID(Platform.Spotify, id),
            PlatformSongID = id is null ? null : new SongID(Platform.Spotify, id),
            SourcePlatform = Platform.Spotify,
            SyncStatus = SyncStatus.Pending,
            MediaType = MediaType.MP3,
            CurrentTrackLocation = id is null ? null : new FileLocation($"https://open.spotify.com/track/{id}"),
            IsLiked = isLiked,
        };
    }

    // ── minimal RFC 4180 CSV: quoted cells, embedded commas/newlines ──

    private static List<List<string>> SplitCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else if (c == '\r') { /* swallow: the 
 that follows ends the row */ }
            else cell.Append(c);
        }

        row.Add(cell.ToString());
        rows.Add(row);
        // A trailing newline adds a phantom empty row; drop it.
        if (rows.Count > 1 && rows[^1].Count == 1 && rows[^1][0].Length == 0)
            rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    /// <summary>
    /// JsonSerializer cannot bind {"tracks": [...]} to an array directly,
    /// so pull the array out of the wrapper by name.
    /// </summary>
    private static string ExtractArray(string content, string arrayName)
    {
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.TryGetProperty(arrayName, out var arr)
            ? arr.GetRawText()
            : "[]";
    }
}
