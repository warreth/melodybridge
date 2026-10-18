using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The account path fills ISRCs from GET /v1/tracks?ids= (playlist items
/// carry simplified tracks without external_ids). The parser must read
/// the real Spotify shape and skip anything incomplete.
/// </summary>
[TestFixture]
public class SpotifyIsrcBatchTests
{
    [Test]
    public void ParseIsrcBatch_ReadsIdAndIsrcPairs()
    {
        using var doc = JsonDocument.Parse("""
        {
          "tracks": [
            { "id": "4uLU6hMCjMI75M1A2tKUQC", "external_ids": { "isrc": "GBARL9300135" } },
            { "id": "7GhIk7Il098yCjg4BQjzvb", "external_ids": { "isrc": "USRC17607839" } }
          ]
        }
        """);

        var pairs = SpotifyAccountProvider.ParseIsrcBatch(doc.RootElement).ToList();

        Assert.That(pairs, Is.EqualTo(new[]
        {
            ("4uLU6hMCjMI75M1A2tKUQC", "GBARL9300135"),
            ("7GhIk7Il098yCjg4BQjzvb", "USRC17607839"),
        }));
    }

    [Test]
    public void ApplyIsrcs_AttachesMatchingIds_AndLeavesTheRestAlone()
    {
        var withPlatform = new Track
        {
            Title = "One",
            SongID = new SongID(Platform.Spotify, "id-1"),
            PlatformSongID = new SongID(Platform.Spotify, "id-1"),
        };
        var withSongOnly = new Track
        {
            Title = "Two",
            SongID = new SongID(Platform.Spotify, "id-2"),
        };
        var unknown = new Track
        {
            Title = "Three",
            SongID = new SongID(Platform.Spotify, "id-3"),
        };

        SpotifyAccountProvider.ApplyIsrcs(new[] { withPlatform, withSongOnly, unknown },
            new Dictionary<string, string> { ["id-1"] = "AAA111", ["id-2"] = "BBB222" });

        Assert.Multiple(() =>
        {
            Assert.That(withPlatform.Isrc, Is.EqualTo("AAA111"));
            Assert.That(withSongOnly.Isrc, Is.EqualTo("BBB222"),
                "a track with only a SongID is matched too");
            Assert.That(unknown.Isrc, Is.Null, "no pair means no ISRC, not a guessed one");
        });
    }

    [Test]
    public void ParseIsrcBatch_SkipsIncompleteEntries()
    {
        using var doc = JsonDocument.Parse("""
        {
          "tracks": [
            null,
            { "external_ids": { "isrc": "GBARL9300135" } },
            { "id": "no-isrc" },
            { "id": "empty-isrc", "external_ids": { "isrc": "" } },
            { "id": "valid", "external_ids": { "isrc": "GBARL0600786" } }
          ]
        }
        """);

        var pairs = SpotifyAccountProvider.ParseIsrcBatch(doc.RootElement).ToList();

        Assert.That(pairs, Has.Count.EqualTo(1));
        Assert.That(pairs[0], Is.EqualTo(("valid", "GBARL0600786")));
    }

    [Test]
    public void ParseIsrcBatch_MissingTracks_IsEmpty()
    {
        using var doc = JsonDocument.Parse("""{ "error": { "status": 404 } }""");
        Assert.That(SpotifyAccountProvider.ParseIsrcBatch(doc.RootElement), Is.Empty);
    }
}
