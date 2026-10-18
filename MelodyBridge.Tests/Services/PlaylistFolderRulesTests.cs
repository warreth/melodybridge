using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The folder-layout rules behind the public PlaylistStore helpers: the
/// sanitized playlist folder name, and which configured folder becomes
/// the effective download or archive folder. Pure rules, no database.
/// </summary>
[TestFixture]
public class PlaylistFolderRulesTests
{
    [Test]
    public void FolderNameFor_KeepsReadableNames()
    {
        Assert.That(PlaylistStore.FolderNameFor("My Summer Hits (2024)"),
            Is.EqualTo("My Summer Hits (2024)"),
            "case, spaces and brackets survive: the folder is user-facing");
        Assert.That(PlaylistStore.FolderNameFor("  Liked songs (Spotify)  "),
            Is.EqualTo("Liked songs (Spotify)"),
            "surrounding whitespace is trimmed");
    }

    [Test]
    public void FolderNameFor_ReplacesEveryCharacterWindowsRejects()
    {
        Assert.That(PlaylistStore.FolderNameFor("AC/DC: Back\\In|Black?*"),
            Is.EqualTo("AC_DC_ Back_In_Black__"),
            "the sanitizing is platform-independent, so a library survives a copy to Windows");
    }

    [Test]
    public void FolderNameFor_ReservedDeviceNames_GetAPrefix()
    {
        Assert.That(PlaylistStore.FolderNameFor("aux"), Is.EqualTo("_aux"));
        Assert.That(PlaylistStore.FolderNameFor("NUL"), Is.EqualTo("_NUL"));
        Assert.That(PlaylistStore.FolderNameFor("Com1"), Is.EqualTo("_Com1"));
        Assert.That(PlaylistStore.FolderNameFor("Concert"), Is.EqualTo("Concert"),
            "only the exact device names are special");
    }

    [Test]
    public void FolderNameFor_BlankAndDottedNames_FallBackCleanly()
    {
        Assert.That(PlaylistStore.FolderNameFor(null), Is.EqualTo("playlist"));
        Assert.That(PlaylistStore.FolderNameFor("   "), Is.EqualTo("playlist"));
        Assert.That(PlaylistStore.FolderNameFor("..."), Is.EqualTo("playlist"),
            "a name that is only dots would collide with the parent folder");
    }

    [Test]
    public void FolderNameFor_VeryLongName_IsTruncated()
    {
        var name = PlaylistStore.FolderNameFor(new string('a', 300));
        Assert.That(name, Has.Length.LessThanOrEqualTo(100));
        Assert.That(name, Is.EqualTo(new string('a', 100)));
    }

    [Test]
    public void EffectivePlaylistFolder_DefaultRoot_GetsThePlaylistFolder()
    {
        var folder = PlaylistStore.EffectivePlaylistFolder(null, "/music", "Road Trip");
        Assert.That(folder, Is.EqualTo(Path.Combine("/music", "Road Trip")));
    }

    [Test]
    public void EffectivePlaylistFolder_StoredDefault_CountsAsTheDefault()
    {
        // Playlists created before each got a folder stored the music path
        // itself; that is not an explicit choice, so the playlist folder
        // still applies.
        var folder = PlaylistStore.EffectivePlaylistFolder("/music/", "/music", "Road Trip");
        Assert.That(folder, Is.EqualTo(Path.Combine("/music", "Road Trip")));
    }

    [Test]
    public void EffectivePlaylistFolder_ExplicitFolder_IsUsedAsItIs()
    {
        var folder = PlaylistStore.EffectivePlaylistFolder("/data/rock", "/music", "Road Trip");
        Assert.That(folder, Is.EqualTo("/data/rock"),
            "a folder the user typed is the folder, no subfolder is appended");
    }

    [Test]
    public void EffectivePlaylistFolder_NothingConfigured_IsEmpty()
    {
        Assert.That(PlaylistStore.EffectivePlaylistFolder(null, "", "Road Trip"), Is.Empty,
            "the caller decides how to refuse; an empty result has no folder to download into");
    }

    [Test]
    public void EffectiveArchiveFolder_GlobalPath_GetsThePlaylistFolder()
    {
        var folder = PlaylistStore.EffectiveArchiveFolder(null, "/archive", "Road Trip");
        Assert.That(folder, Is.EqualTo(Path.Combine("/archive", "Road Trip")));
    }

    [Test]
    public void EffectiveArchiveFolder_ExplicitFolder_IsUsedAsItIs()
    {
        var folder = PlaylistStore.EffectiveArchiveFolder("/archive/own", "/archive", "Road Trip");
        Assert.That(folder, Is.EqualTo("/archive/own"));
    }

    [Test]
    public void EffectiveArchiveFolder_NoArchivePath_IsEmpty()
    {
        Assert.That(PlaylistStore.EffectiveArchiveFolder(null, "", "Road Trip"), Is.Empty);
    }
}
