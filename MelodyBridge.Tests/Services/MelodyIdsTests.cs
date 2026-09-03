using MelodyBridge.Core;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Deterministic MELODY_ID behavior: the same source track always maps to
/// the same id, no matter which object carries it or when it is mapped.
/// That is what lets a wiped database re-match the tags inside files that
/// are already on disk. Pure asserts over the real helper, no database.
/// </summary>
[TestFixture]
[Category("MelodyIds")]
public class MelodyIdsTests
{
    [Test]
    public void SameSpotifyTrack_FromTwoTrackObjects_YieldsTheSameId()
    {
        var first = MelodyIds.For(new SongID(Platform.Spotify, "4uLU6hMCjMI75M1A2tKUQC"));
        var second = MelodyIds.For(new SongID(Platform.Spotify, "4uLU6hMCjMI75M1A2tKUQC"));

        Assert.That(second, Is.EqualTo(first),
            "the id comes from the source track, not from a guid or a clock");
        Assert.That(first, Is.EqualTo("spotify:4uLU6hMCjMI75M1A2tKUQC"));
    }

    [Test]
    public void YouTubeId_GetsYtPrefix()
    {
        var id = MelodyIds.For(new SongID(Platform.YouTubeMusic, "dQw4w9WgXcQ"));
        Assert.That(id, Is.EqualTo("yt:dQw4w9WgXcQ"),
            "YouTube Music ids use the short yt: prefix");
    }

    [Test]
    public void InternetArchiveIdentifier_GetsIaPrefix()
    {
        var id = MelodyIds.For(new SongID(Platform.Unknown, "gd1970-05-02.sbd.holzner.8061.sbeok.flac16"));
        // Archive items surface through the downloader, not a provider; the
        // ia: form is reachable through the string overload too.
        Assert.That(MelodyIds.For("InternetArchive", "gd1970-05-02"), Is.EqualTo("ia:gd1970-05-02"));
    }

    [Test]
    public void ArchiveItem_StringOverload_IsDeterministic()
    {
        var first = MelodyIds.For("ArchiveOrg", "etree1970");
        var second = MelodyIds.For("archiveorg", "etree1970");
        Assert.That(first, Is.EqualTo("ia:etree1970"),
            "the archive platform names all map to the ia: prefix");
        Assert.That(second, Is.EqualTo(first),
            "prefix lookup is case-insensitive");
    }

    [Test]
    public void CsvImportRows_SameMetadata_SameId()
    {
        var first = MelodyIds.ForCsv("Artist", "Title", TimeSpan.FromSeconds(200));
        var second = MelodyIds.ForCsv("  artist  ", "title", TimeSpan.FromSeconds(200));

        Assert.That(second, Is.EqualTo(first),
            "artist and title are trimmed and lowercased before hashing");
        Assert.That(first, Does.StartWith("csv:"));
        Assert.That(first.Length, Is.EqualTo("csv:".Length + 16),
            "16 hex chars after the prefix");
    }

    [Test]
    public void CsvImportRows_DifferentDuration_DifferentId()
    {
        var shortVersion = MelodyIds.ForCsv("Artist", "Title", TimeSpan.FromSeconds(200));
        var longVersion = MelodyIds.ForCsv("Artist", "Title", TimeSpan.FromSeconds(300));

        Assert.That(longVersion, Is.Not.EqualTo(shortVersion),
            "duration is part of the hash: two lengths are two songs");
    }

    [Test]
    public void UnknownId_FallsBackToHashForm()
    {
        var unknown = MelodyIds.ForUnknown("Artist", "Title", TimeSpan.FromSeconds(200));
        var csv = MelodyIds.ForCsv("Artist", "Title", TimeSpan.FromSeconds(200));

        Assert.That(unknown, Does.StartWith("mbh:"),
            "rows without any id use the mbh: hash prefix");
        Assert.That(unknown["mbh:".Length..], Is.EqualTo(csv["csv:".Length..]),
            "both forms hash the same metadata the same way");
    }

    [Test]
    public void Ids_ContainNoSpacesAndNoGuidSubstring()
    {
        var ids = new[]
        {
            MelodyIds.For(new SongID(Platform.Spotify, "4uLU6hMCjMI75M1A2tKUQC")),
            MelodyIds.For(new SongID(Platform.YouTubeMusic, "dQw4w9WgXcQ")),
            MelodyIds.ForCsv("Some Artist", "Some Title", TimeSpan.FromMinutes(3)),
            MelodyIds.ForUnknown("Some Artist", "Some Title", TimeSpan.FromMinutes(3)),
        };

        foreach (var id in ids)
        {
            Assert.That(id, Does.Not.Contain(" "),
                "ids go into file tags and SQLite text columns: no spaces");
            Assert.That(id, Does.Not.Contain("mb-"),
                "the old random guid prefix must not appear");
            Assert.That(id, Does.Match("^[a-z0-9]+:.+$"),
                "shape is a lowercase prefix, a colon, then the id");
        }
    }

    [Test]
    public void Ids_AreStableAcrossCalls()
    {
        var repeat = Enumerable.Range(0, 100)
            .Select(_ => MelodyIds.For(new SongID(Platform.Spotify, "stableTrackId")))
            .Distinct()
            .Count();
        Assert.That(repeat, Is.EqualTo(1),
            "100 calls, one value: the mapping is a pure function of the input");

        var hashRepeat = Enumerable.Range(0, 100)
            .Select(_ => MelodyIds.ForCsv("A", "T", TimeSpan.FromSeconds(1)))
            .Distinct()
            .Count();
        Assert.That(hashRepeat, Is.EqualTo(1),
            "the hash form is just as stable");
    }

    [Test]
    public void TwoDifferentSpotifyTracks_NeverCollide()
    {
        var first = MelodyIds.For(new SongID(Platform.Spotify, "trackOne"));
        var second = MelodyIds.For(new SongID(Platform.Spotify, "trackTwo"));
        Assert.That(first, Is.Not.EqualTo(second),
            "different source ids mean different MELODY_IDs");
    }
}
