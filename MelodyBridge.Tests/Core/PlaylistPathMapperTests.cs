using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

/// <summary>
/// The shared output path mapper: prefix remap for mounts, extension
/// remap for formats. The extension rules used to be merged into the
/// prefix map and never matched, which made the feature a no-op.
/// </summary>
[TestFixture]
public class PlaylistPathMapperTests
{
    private static PlaylistOutputOptions Options(
        Dictionary<string, string>? pathRemap = null,
        Dictionary<string, string>? extensionRemap = null)
        => new("out.m3u", false, pathRemap, ExtensionRemap: extensionRemap);

    [Test]
    public void NoRules_ReturnsThePathUnchanged()
    {
        Assert.That(PlaylistPathMapper.Apply("/music/song.flac", Options()),
            Is.EqualTo("/music/song.flac"));
    }

    [Test]
    public void NullPath_StaysNull()
    {
        Assert.That(PlaylistPathMapper.Apply(null, Options()), Is.Null);
    }

    [Test]
    public void PrefixRule_ReplacesTheMount()
    {
        var options = Options(pathRemap: new Dictionary<string, string> { ["/music"] = "/mnt/music" });

        Assert.That(PlaylistPathMapper.Apply("/music/album/song.flac", options),
            Is.EqualTo("/mnt/music/album/song.flac"));
    }

    [Test]
    public void PrefixRule_IsCaseInsensitive()
    {
        var options = Options(pathRemap: new Dictionary<string, string> { ["/MUSIC"] = "/mnt" });

        Assert.That(PlaylistPathMapper.Apply("/music/song.flac", options),
            Is.EqualTo("/mnt/song.flac"));
    }

    [Test]
    public void PrefixRule_DoesNotEatASiblingsDirectory()
    {
        var options = Options(pathRemap: new Dictionary<string, string>
        {
            ["/music"] = "/mnt/music",
            ["/music2"] = "/mnt/music2",
        });

        Assert.Multiple(() =>
        {
            Assert.That(PlaylistPathMapper.Apply("/music2/album/song.flac", options),
                Is.EqualTo("/mnt/music2/album/song.flac"),
                "a rule for /music must not swallow the longer sibling /music2");
            Assert.That(PlaylistPathMapper.Apply("/music/album/song.flac", options),
                Is.EqualTo("/mnt/music/album/song.flac"),
                "and the shorter rule still applies to its own folder");
        });
    }

    [Test]
    public void PrefixRule_ExactMatch_RewritesWithoutTheSeparator()
    {
        var options = Options(pathRemap: new Dictionary<string, string>
        {
            ["/music/song.flac"] = "/mnt/music/song.flac",
        });

        Assert.That(PlaylistPathMapper.Apply("/music/song.flac", options),
            Is.EqualTo("/mnt/music/song.flac"));
    }

    [Test]
    public void ExtensionRule_ReplacesOnlyTheExtension()
    {
        var options = Options(extensionRemap: new Dictionary<string, string> { [".flac"] = ".opus" });

        Assert.That(PlaylistPathMapper.Apply("/music/band - song.flac", options),
            Is.EqualTo("/music/band - song.opus"),
            "the rule applies to the extension, never as a path prefix");
    }

    [Test]
    public void ExtensionRule_AcceptsKeysWithoutADot_AndAnyCase()
    {
        var options = Options(extensionRemap: new Dictionary<string, string> { ["FLAC"] = "mp3" });

        Assert.That(PlaylistPathMapper.Apply("/music/song.FLAC", options),
            Is.EqualTo("/music/song.mp3"));
    }

    [Test]
    public void UnrelatedExtension_IsLeftAlone()
    {
        var options = Options(extensionRemap: new Dictionary<string, string> { [".flac"] = ".opus" });

        Assert.That(PlaylistPathMapper.Apply("/music/song.mp3", options),
            Is.EqualTo("/music/song.mp3"));
    }

    [Test]
    public void BothRules_ApplyPrefixThenExtension()
    {
        var options = Options(
            pathRemap: new Dictionary<string, string> { ["/music"] = "/mnt/music" },
            extensionRemap: new Dictionary<string, string> { [".flac"] = ".opus" });

        Assert.That(PlaylistPathMapper.Apply("/music/album/song.flac", options),
            Is.EqualTo("/mnt/music/album/song.opus"));
    }
}
