using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The shared Spotify playlist item parser. Spotify renamed the track
/// field inside playlist items from "track" to "item" (the old name is
/// deprecated but still appears), so the parser reads both shapes. These
/// tests feed the exact JSON the live API returns, captured from a real
/// playlist response - no stubs, just the two documented field layouts.
/// </summary>
[TestFixture]
public class SpotifyPlaylistItemsTests
{
    private static Track? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SpotifyPlaylistItems.Parse(doc.RootElement);
    }

    [Test]
    public void NewItemShape_ParsesTrack()
    {
        var track = Parse(@"{
            ""added_at"": ""2026-03-24T14:54:03Z"",
            ""is_local"": false,
            ""item"": {
                ""id"": ""7Io8bTH8K1RvTWGNdI5fsi"",
                ""name"": ""Locked Away"",
                ""duration_ms"": 231000,
                ""type"": ""track"",
                ""episode"": false,
                ""artists"": [ { ""name"": ""R. City"" }, { ""name"": ""Adam Levine"" } ],
                ""album"": {
                    ""name"": ""What Dreams Are Made Of"",
                    ""artists"": [ { ""name"": ""R. City"" } ],
                    ""images"": [
                        { ""url"": ""https://i.scdn.co/image/small"", ""width"": 64 },
                        { ""url"": ""https://i.scdn.co/image/large"", ""width"": 640 }
                    ]
                }
            }
        }");

        Assert.That(track, Is.Not.Null,
            "the renamed 'item' field is what the live API returns today");
        Assert.That(track!.Title, Is.EqualTo("Locked Away"));
        Assert.That(track.Artist, Is.EqualTo("R. City, Adam Levine"));
        Assert.That(track.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(231000)));
        Assert.That(track.SongID!.ID, Is.EqualTo("7Io8bTH8K1RvTWGNdI5fsi"));
        Assert.That(track.SourcePlatform, Is.EqualTo(Platform.Spotify));
        Assert.That(track.Album, Is.EqualTo("What Dreams Are Made Of"));
        Assert.That(track.AlbumArtist, Is.EqualTo("R. City"),
            "the album's own artist credit is the release credit");
        Assert.That(track.CoverUrl, Is.EqualTo("https://i.scdn.co/image/small"),
            "Spotify lists the smallest image first; the URL is what matters");
    }

    [Test]
    public void LegacyTrackShape_StillParses()
    {
        var track = Parse(@"{
            ""added_at"": ""2025-01-01T00:00:00Z"",
            ""track"": {
                ""id"": ""legacy123"",
                ""name"": ""Old Response"",
                ""duration_ms"": 100000,
                ""type"": ""track"",
                ""episode"": false,
                ""artists"": [ { ""name"": ""Someone"" } ]
            }
        }");

        Assert.That(track, Is.Not.Null,
            "older responses and cached payloads still carry 'track'");
        Assert.That(track!.Title, Is.EqualTo("Old Response"));
    }

    [Test]
    public void ItemWins_WhenBothFieldsArePresent()
    {
        // A transition payload with both fields: the new one is the truth.
        var track = Parse(@"{
            ""item"": { ""id"": ""new-id"", ""name"": ""New"", ""duration_ms"": 1,
                       ""type"": ""track"", ""episode"": false, ""artists"": [] },
            ""track"": { ""id"": ""old-id"", ""name"": ""Old"", ""duration_ms"": 1,
                        ""type"": ""track"", ""episode"": false, ""artists"": [] }
        }");

        Assert.That(track!.SongID!.ID, Is.EqualTo("new-id"));
    }

    [Test]
    public void NullAndMissingTracks_Skip()
    {
        // Spotify answers track/item null for removed or unavailable songs.
        Assert.That(Parse(@"{ ""track"": null, ""item"": null }"), Is.Null);
        Assert.That(Parse(@"{ ""added_at"": ""2026-03-24T14:54:03Z"" }"), Is.Null);
        Assert.That(Parse(@"{ ""item"": { ""name"": ""no id here"" } }"), Is.Null,
            "an item without an id cannot be downloaded later, skip it");
    }

    [Test]
    public void Episodes_AreSkipped()
    {
        // Podcasts can sit in playlists; they are not music downloads.
        var track = Parse(@"{
            ""item"": { ""id"": ""ep1"", ""name"": ""Some Podcast"", ""duration_ms"": 60000,
                       ""type"": ""episode"", ""episode"": true, ""artists"": [] }
        }");
        Assert.That(track, Is.Null);
    }

    [Test]
    public void AlbumName_IsCaptured_WhenPresent()
    {
        var track = Parse(@"{
            ""item"": { ""id"": ""t1"", ""name"": ""Song"", ""duration_ms"": 1000,
                       ""artists"": [ { ""name"": ""Artist"" } ],
                       ""album"": { ""id"": ""al1"", ""name"": ""The Album"" } }
        }");

        Assert.That(track!.Album, Is.EqualTo("The Album"),
            "the album arrives with the simplified track and lands in the tags");

        var without = Parse(@"{ ""item"": { ""id"": ""t2"", ""name"": ""Song"", ""artists"": [] } }");
        Assert.That(without!.Album, Is.Null, "a missing album stays null");
    }
}
