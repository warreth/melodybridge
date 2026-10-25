using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The file name pattern: tokens expand, empty tokens do not leave stray
/// separators, and an empty pattern means "keep the source name".
/// </summary>
[TestFixture]
public class FileNamePatternTests
{
    [Test]
    public void DefaultPattern_IsArtistThenTitle()
    {
        var name = FileNamePattern.Expand(
            FileNamePattern.Default, "Daft Punk", "Get Lucky");
        Assert.That(name, Is.EqualTo("Daft Punk - Get Lucky"));
    }

    [Test]
    public void Tokens_ExpandArtistTitleAlbumAndTrack()
    {
        var name = FileNamePattern.Expand(
            "{track} - {artist} - {title} ({album})",
            "Daft Punk", "Get Lucky", "Random Access Memories", 3);
        Assert.That(name, Is.EqualTo("03 - Daft Punk - Get Lucky (Random Access Memories)"));
    }

    [TestCase("{source}")]
    [TestCase(" {SOURCE} ")]
    public void SourceToken_KeepsThePluginName(string pattern)
        => Assert.That(FileNamePattern.Expand(pattern, "A", "T"), Is.Null);

    [Test]
    public void TitleOnlyPattern_IsTheTitle()
    {
        var name = FileNamePattern.Expand("{title}", "Daft Punk", "Get Lucky");
        Assert.That(name, Is.EqualTo("Get Lucky"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void EmptyPattern_KeepsTheSourceName(string? pattern)
        => Assert.That(FileNamePattern.Expand(pattern, "A", "T"), Is.Null);

    [Test]
    public void MissingArtist_DoesNotLeaveALeadingSeparator()
    {
        var name = FileNamePattern.Expand("{artist} - {title}", "", "Get Lucky");
        Assert.That(name, Is.EqualTo("Get Lucky"));
    }

    [Test]
    public void MissingAlbum_DoesNotLeaveAnEmptyPair()
    {
        var name = FileNamePattern.Expand(
            "{artist} - {album} - {title}", "A", "T", album: null);
        Assert.That(name, Is.EqualTo("A - T"));
    }

    [Test]
    public void PatternWithoutTokens_IsSanitizedButKept()
    {
        var name = FileNamePattern.Expand("mix: live/set", "A", "T");
        Assert.That(name, Is.EqualTo("mix_ live_set"),
            "separators and reserved characters are sanitized like every file the app writes");
    }

    [Test]
    public void BadPathShapedPattern_FallsBackToTheTitle()
    {
        var name = FileNamePattern.Expand("../..", "A", "T");
        Assert.That(name, Is.EqualTo("T"),
            "a pattern that sanitizes to nothing must not escape the folder or yield an empty name");
    }

    [Test]
    public void TrackToken_IsTwoDigits()
    {
        Assert.That(FileNamePattern.Expand("{track}", "A", "T", track: 7), Is.EqualTo("07"));
        Assert.That(FileNamePattern.Expand("{track} - {title}", "A", "T", track: 120),
            Is.EqualTo("120 - T"));
    }
}
