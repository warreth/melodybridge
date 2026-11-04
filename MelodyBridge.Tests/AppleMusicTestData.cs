using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.AppleMusic;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests;

/// <summary>
/// Small, real-shaped Apple Music payloads for the unit tests. They keep the
/// field names and nesting the live API and page use, without shipping a
/// multi-megabyte capture.
/// </summary>
internal static class AppleMusicTestData
{
    public const string PlaylistId = "pl.test";
    public const string SharedPlaylistId = "pl.u-test";

    public static string Token(string issuer = "AMPWebPlay", int days = 30)
    {
        var header = Base64Url("""{"alg":"ES256","typ":"JWT"}""");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            iss = issuer,
            exp = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds(),
        }));
        return $"{header}.{payload}.signature";
    }

    public static string HomeHtml => """
        <!doctype html><html><head>
        <script type="module" src="/assets/index~test.js"></script>
        </head><body>Apple Music</body></html>
        """;

    public static string Bundle => $"""
        const first="{Token("OtherToken")}";
        const second="{Token("AMPWebPlay")}";
        """;

    public static string PlaylistApiJson(
        string playlistId = PlaylistId,
        string? next = null,
        params (string Id, string Title, string Artist, string Isrc)[] tracks)
    {
        var items = tracks.Length == 0
            ? new[] { ("11", "One", "Artist One", "USAAA0000011") }
            : tracks;

        var data = new
        {
            data = new[]
            {
                new
                {
                    id = playlistId,
                    type = "playlists",
                    attributes = new
                    {
                        name = "API List",
                        curatorName = "Curator",
                        description = new { standard = "A test playlist" },
                        artwork = new { url = "https://is1.mzstatic.test/image/thumb/Features/v4/x/{w}x{h}bb.{f}" },
                        url = $"https://music.apple.com/us/playlist/{playlistId}",
                    },
                    relationships = new
                    {
                        tracks = new
                        {
                            data = items.Select(track => new
                            {
                                id = track.Item1,
                                type = "songs",
                                attributes = new
                                {
                                    name = track.Item2,
                                    artistName = track.Item3,
                                    albumName = "Album",
                                    albumArtistName = "Album Artist",
                                    isrc = track.Item4,
                                    durationInMillis = 123000,
                                    artwork = new { url = "https://is1.mzstatic.test/image/thumb/Music/v4/y/{w}x{h}bb.{f}" },
                                    url = $"https://music.apple.com/us/album/track/1?i={track.Item1}",
                                },
                            }).ToArray(),
                            next,
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(data);
    }

    public static string TracksPageJson(
        string? next = null,
        params (string Id, string Title, string Artist, string Isrc)[] tracks)
    {
        var data = new
        {
            data = tracks.Select(track => new
            {
                id = track.Id,
                type = "songs",
                attributes = new
                {
                    name = track.Title,
                    artistName = track.Artist,
                    albumName = "Album",
                    albumArtistName = "Album Artist",
                    isrc = track.Isrc,
                    durationInMillis = 123000,
                    artwork = new { url = "https://is1.mzstatic.test/image/thumb/Music/v4/y/{w}x{h}bb.{f}" },
                    url = $"https://music.apple.com/us/album/track/1?i={track.Id}",
                },
            }).ToArray(),
            next,
        };
        return JsonSerializer.Serialize(data);
    }

    public static string PlaylistPageHtml(
        string playlistId = PlaylistId,
        int declaredCount = 2,
        int trackCount = 2)
    {
        var tracks = Enumerable.Range(1, trackCount).Select(index => new
        {
            id = $"track-lockup - {playlistId} - {index}",
            title = $"Page Track {index}",
            artistName = $"Page Artist {index}",
            duration = 180000,
            contentDescriptor = new
            {
                kind = "song",
                url = $"https://music.apple.com/us/album/page-track/1?i={index}",
                identifiers = new { storeAdamID = index.ToString() },
            },
            artwork = new
            {
                dictionary = new
                {
                    url = "https://is1.mzstatic.test/image/thumb/Music/v4/z/{w}x{h}bb.{f}",
                },
            },
            tertiaryLinks = new[] { new { title = "Page Album" } },
        }).ToArray();

        var header = new
        {
            id = $"playlist-detail-header-section - {playlistId}",
            items = new[]
            {
                new
                {
                    title = "Page List",
                    trackCount = declaredCount,
                    subtitleLinks = new[] { new { title = "Page Curator" } },
                    artwork = new
                    {
                        dictionary = new
                        {
                            url = "https://is1.mzstatic.test/image/thumb/Features/v4/p/{w}x{h}bb.{f}",
                        },
                    },
                },
            },
        };

        var trackList = new
        {
            id = $"track-list - {playlistId}",
            items = tracks,
        };

        var footer = new
        {
            id = $"track-list-footer-section - {playlistId}",
            items = new[] { new { description = "2 songs" } },
        };

        var payload = new
        {
            data = new[]
            {
                new
                {
                    data = new
                    {
                        sections = new object[] { header, trackList, footer },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(payload);
        return $"<html><body><script id=\"serialized-server-data\" type=\"application/json\">{json}</script></body></html>";
    }

    public static string LibraryPlaylistsJson(
        string? next = null,
        params (string LibraryId, string? GlobalId, string Name, string Owner, int TrackCount, bool CanEdit)[] rows)
    {
        var data = rows.Length == 0
            ? new[]
            {
                ("p.one", (string?)"pl.one", "Shared List", "Owner", 3, true),
            }
            : rows;

        var payload = new
        {
            data = data.Select(row => new
            {
                id = row.Item1,
                type = "library-playlists",
                attributes = new
                {
                    name = row.Item3,
                    curatorName = row.Item4,
                    trackCount = row.Item5,
                    canEdit = row.Item6,
                    artwork = new { url = "https://is1.mzstatic.test/image/thumb/Music/v4/l/{w}x{h}bb.{f}" },
                    playParams = new { globalId = row.Item2 },
                },
            }).ToArray(),
            meta = new { total = data.Length },
            next,
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string LibraryPlaylistJson(string libraryId = "p.one")
    {
        var payload = new
        {
            data = new[]
            {
                new
                {
                    id = libraryId,
                    type = "library-playlists",
                    attributes = new
                    {
                        name = "Private List",
                        curatorName = "Owner",
                        playParams = new { globalId = "pl.one" },
                    },
                    relationships = new
                    {
                        tracks = new
                        {
                            data = new[]
                            {
                                new
                                {
                                    id = "i.track1",
                                    type = "library-songs",
                                    attributes = new
                                    {
                                        name = "Library One",
                                        artistName = "Library Artist",
                                        albumName = "Library Album",
                                        isrc = "USBBB0000001",
                                        durationInMillis = 90000,
                                        playParams = new { catalogId = "22" },
                                        artwork = new { url = "https://is1.mzstatic.test/image/thumb/Music/v4/k/{w}x{h}bb.{f}" },
                                        url = "https://music.apple.com/us/album/library-one/2?i=22",
                                    },
                                },
                            },
                            next = (string?)null,
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string LibrarySongsJson(
        string? next = null,
        params (string Id, string Title, string CatalogId, string Isrc)[] rows)
    {
        var data = rows.Length == 0
            ? new[] { ("i.one", "Saved One", "31", "USCCC0000031") }
            : rows;

        var payload = new
        {
            data = data.Select(row => new
            {
                id = row.Item1,
                type = "library-songs",
                attributes = new
                {
                    name = row.Item2,
                    artistName = "Saved Artist",
                    albumName = "Saved Album",
                    isrc = row.Item4,
                    durationInMillis = 120000,
                    playParams = new { catalogId = row.Item3 },
                    artwork = new { url = "https://is1.mzstatic.test/image/thumb/Music/v4/s/{w}x{h}bb.{f}" },
                },
            }).ToArray(),
            meta = new { total = data.Length },
            next,
        };
        return JsonSerializer.Serialize(payload);
    }

    public static AppleMusicWebTokenProvider TokenProvider(ScriptedHandler handler)
        => new(new HttpClient(handler), NullLogger<AppleMusicWebTokenProvider>.Instance);

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
