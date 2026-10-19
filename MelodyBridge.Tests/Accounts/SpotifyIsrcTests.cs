using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Services;
using SpotifyAPI.Web;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The ISRC rides along in the payload both account paths already fetch:
/// the saved-track object for liked songs and the playlist item for
/// playlists. Spotify's February 2026 changes removed the
/// GET /v1/tracks?ids= batch endpoint for development-mode apps, so the
/// second pass that used to fill ISRCs is gone and these payloads are the
/// only source; a missing value must stay null instead of guessed.
/// </summary>
[TestFixture]
public class SpotifyIsrcTests
{
    private static Track? ParseItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SpotifyPlaylistItems.Parse(doc.RootElement);
    }

    [Test]
    public void PlaylistItem_CarriesTheIsrcFromExternalIds()
    {
        var track = ParseItem("""
        {
            "added_at": "2026-03-24T14:54:03Z",
            "is_local": false,
            "item": {
                "id": "7Io8bTH8K1RvTWGNdI5fsi",
                "name": "Locked Away",
                "duration_ms": 231000,
                "type": "track",
                "episode": false,
                "external_ids": { "isrc": "USRC11501767" },
                "artists": [ { "name": "R. City" } ]
            }
        }
        """);

        Assert.That(track, Is.Not.Null);
        Assert.That(track!.Isrc, Is.EqualTo("USRC11501767"),
            "the playlist item object carries external_ids, no second call needed");
    }

    [TestCase("""{ "item": { "id": "abc", "name": "No ids" } }""", TestName = "PlaylistItem_WithoutExternalIds_LeavesTheIsrcNull")]
    [TestCase("""{ "item": { "id": "abc", "name": "Blank", "external_ids": { "isrc": "" } } }""", TestName = "PlaylistItem_WithBlankIsrc_LeavesTheIsrcNull")]
    [TestCase("""{ "item": { "id": "abc", "name": "Other ids", "external_ids": { "ean": "123" } } }""", TestName = "PlaylistItem_WithOnlyOtherIds_LeavesTheIsrcNull")]
    public void PlaylistItem_WithoutAnIsrc_StaysNull(string json)
    {
        var track = ParseItem(json);

        Assert.That(track, Is.Not.Null);
        Assert.That(track!.Isrc, Is.Null, "an absent ISRC is never invented");
    }

    [Test]
    public void SavedTrack_CarriesTheIsrcFromTheSdkExternalIds()
    {
        // The SDK's [Obsolete] on this field mirrors Spotify's February
        // 2026 removal notice; the March 2026 changelog reverted it.
#pragma warning disable CS0618
        var full = new FullTrack
        {
            Id = "4uLU6hMCjMI75M1A2tKUQC",
            Name = "Die With A Smile",
            DurationMs = 252000,
            Artists = new List<SimpleArtist> { new() { Name = "Lady Gaga" } },
            ExternalIds = new Dictionary<string, string> { ["isrc"] = "USUM72409279" },
        };
#pragma warning restore CS0618

        var track = SpotifyAccountProvider.MapSavedTrack(full);

        Assert.That(track.Isrc, Is.EqualTo("USUM72409279"));
    }

    [Test]
    public void SavedTrack_WithoutExternalIds_StaysNull()
    {
#pragma warning disable CS0618
        var full = new FullTrack
        {
            Id = "4uLU6hMCjMI75M1A2tKUQC",
            Name = "Unknown",
            Artists = new List<SimpleArtist> { new() { Name = "Someone" } },
            ExternalIds = new Dictionary<string, string>(),
        };
#pragma warning restore CS0618

        Assert.That(SpotifyAccountProvider.MapSavedTrack(full).Isrc, Is.Null);
    }

    [TestCase(null, TestName = "ReadIsrc_NoDictionary_IsNull")]
    public void ReadIsrc_MissingOrBlank_IsNull(IReadOnlyDictionary<string, string>? ids)
    {
        Assert.That(SpotifyAccountProvider.ReadIsrc(ids), Is.Null);
    }

    [Test]
    public void ReadIsrc_ReadsAValueAndRejectsBlankOnes()
    {
        var ids = new Dictionary<string, string>
        {
            ["isrc"] = "GBARL9300135",
            ["ean"] = "123",
        };
        Assert.That(SpotifyAccountProvider.ReadIsrc(ids), Is.EqualTo("GBARL9300135"));

        var blank = new Dictionary<string, string> { ["isrc"] = "   " };
        Assert.That(SpotifyAccountProvider.ReadIsrc(blank), Is.Null,
            "whitespace is not an ISRC");
    }
}
