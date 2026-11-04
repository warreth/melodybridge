using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.AppleMusic;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class AppleMusicUrlsTests
{
    [TestCase("pl.f4d106fed2bd41149aaacabb233eb5eb", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [TestCase("pl.u-vxy6693I1l0Nko", "pl.u-vxy6693I1l0Nko")]
    [TestCase("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [TestCase("https://music.apple.com/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [TestCase("https://music.apple.com/us/playlist/pl.f4d106fed2bd41149aaacabb233eb5eb", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [TestCase("https://itunes.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [TestCase("https://music.apple.com/gb/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb?l=en-GB", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    public void Parse_PlaylistInputs_ReturnsId(string input, string expected)
    {
        var reference = AppleMusicUrls.Parse(input);
        Assert.That(reference.Id, Is.EqualTo(expected));
        Assert.That(reference.NeedsAccount, Is.False);
    }

    [TestCase("https://music.apple.com/gb/playlist/todays-hits/pl.test", "gb")]
    [TestCase("https://music.apple.com/playlist/todays-hits/pl.test", "us")]
    [TestCase("https://music.apple.com/us/playlist/pl.test", "us")]
    public void Parse_Storefront_ComesFromTheUrlOrDefaultsToUs(string input, string expected)
    {
        var reference = AppleMusicUrls.Parse(input);
        Assert.That(reference.Storefront, Is.EqualTo(expected));
        Assert.That(reference.StorefrontFromUrl,
            Is.EqualTo(input.Contains("/us/", StringComparison.Ordinal)
                       || input.Contains("/gb/", StringComparison.Ordinal)));
    }

    [Test]
    public void Parse_LibraryMarker_NeedsAnAccount()
    {
        var reference = AppleMusicUrls.Parse("applemusic:library:p.abc123");
        Assert.Multiple(() =>
        {
            Assert.That(reference.Id, Is.EqualTo("p.abc123"));
            Assert.That(reference.IsLibrary, Is.True);
            Assert.That(reference.NeedsAccount, Is.True);
            Assert.That(reference.SourceUrl, Is.EqualTo("applemusic:library:p.abc123"));
        });
    }

    [Test]
    public void Parse_LikedMarker_NeedsAnAccount()
    {
        var reference = AppleMusicUrls.Parse("applemusic:library-songs");
        Assert.Multiple(() =>
        {
            Assert.That(reference.IsLibrarySongs, Is.True);
            Assert.That(reference.NeedsAccount, Is.True);
            Assert.That(reference.SourceUrl, Is.EqualTo("applemusic:library-songs"));
        });
    }

    [TestCase("https://music.apple.com/us/album/never-gonna-give-you-up/1624945511?i=1624945512")]
    [TestCase("https://music.apple.com/us/song/never-gonna-give-you-up/1624945512")]
    [TestCase("https://music.apple.com/us/artist/rick-astley/669771")]
    public void Parse_AlbumSongAndArtistLinks_AreRefused(string input)
    {
        Assert.That(AppleMusicUrls.CanHandle(input), Is.False);
        Assert.Throws<ArgumentException>(() => AppleMusicUrls.Parse(input));
    }

    [Test]
    public void SourceUrlFor_LibraryId_UsesTheAccountMarker()
        => Assert.That(AppleMusicAccountProvider.SourceUrlFor("p.abc"),
            Is.EqualTo("applemusic:library:p.abc"));

    [Test]
    public void SourceUrlFor_CatalogId_UsesThePublicUrl()
        => Assert.That(AppleMusicAccountProvider.SourceUrlFor("pl.abc"),
            Is.EqualTo("https://music.apple.com/us/playlist/pl.abc"));

    [Test]
    public void SourceUrlFor_CatalogId_KeepsAKnownStorefront()
        => Assert.That(AppleMusicAccountProvider.SourceUrlFor("pl.abc", "gb"),
            Is.EqualTo("https://music.apple.com/gb/playlist/pl.abc"));
}
