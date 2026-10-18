using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

/// <summary>
/// Search term normalization: the cleanup every provider query goes
/// through. Pure string logic, so these are plain unit tests, but the
/// cases are the real-world shapes playlists bring: parenthetical mix
/// suffixes, featuring tags, diacritics, empty-after-cleanup titles.
/// </summary>
[Category("Core")]
public class SearchTermsTests
{
    [TestCase("Nightshift (Original Mix)", "Nightshift")]
    [TestCase("Nightshift [Radio Edit]", "Nightshift")]
    [TestCase("Around the World (La La La La La) (Radio Version)", "Around the World La La La La La")]
    [TestCase("One More Time - Radio Edit", "One More Time - Radio Edit")]
    public void CleanTitle_DropsParentheticalClutter(string input, string expected)
        => Assert.That(SearchTerms.CleanTitle(input), Is.EqualTo(expected));

    [TestCase("The Last Airbender (VIP)", "The Last Airbender VIP")]
    [TestCase("Song (Acoustic)", "Song Acoustic")]
    [TestCase("Love Story (Taylor's Version)", "Love Story Taylor's Version")]
    public void CleanTitle_KeepsVersionMarkers(string input, string expected)
        => Assert.That(SearchTerms.CleanTitle(input), Is.EqualTo(expected),
            "a version marker distinguishes recordings; dropping it made searches return the wrong one");

    [TestCase("Temperature ft. Sasha", "Temperature")]
    [TestCase("Temperature feat. Sasha", "Temperature")]
    [TestCase("Umbrella featuring JAY-Z", "Umbrella")]
    [TestCase("Stan (Album Version) ft. Dido", "Stan")]
    public void CleanTitle_DropsFeaturingTags(string input, string expected)
        => Assert.That(SearchTerms.CleanTitle(input), Is.EqualTo(expected));

    [TestCase("Für Elise", "Fur Elise")]
    [TestCase("Bébé Modelée", "Bebe Modelee")]
    [TestCase("Naïve", "Naive")]
    public void CleanTitle_FoldsDiacritics(string input, string expected)
        => Assert.That(SearchTerms.CleanTitle(input), Is.EqualTo(expected));

    [TestCase("Köln   Beats", "Koln Beats")]
    [TestCase("Århus Aftershock", "Arhus Aftershock")]
    public void CleanArtist_FoldsDiacriticsAndCollapses(string input, string expected)
        => Assert.That(SearchTerms.CleanArtist(input), Is.EqualTo(expected));

    [Test]
    public void CleanArtist_KeepsTheFullCredit()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SearchTerms.CleanArtist("Duke Dumont ft. Jax Jones"), Is.EqualTo("Duke Dumont"),
                "a featuring tag still narrows to the primary artist");
            Assert.That(SearchTerms.CleanArtist("Ben Böhmer, Alban Chastel"),
                Is.EqualTo("Ben Bohmer, Alban Chastel"),
                "a comma is a credit separator in some rows, so it is kept");
            Assert.That(SearchTerms.CleanArtist("Earth, Wind & Fire"), Is.EqualTo("Earth, Wind & Fire"),
                "a comma inside the band name must never truncate it to 'Earth'");
            Assert.That(SearchTerms.CleanArtist("Tyler, The Creator"), Is.EqualTo("Tyler, The Creator"));
        });
    }

    [Test]
    public void CleanTitle_NeverEmptiesTheTerm()
    {
        // Everything here would strip away; the original must survive.
        Assert.That(SearchTerms.CleanTitle("(Original Mix)"), Is.Not.Empty);
        Assert.That(SearchTerms.CleanTitle("feat. Someone"), Is.Not.Empty);
        // Empty stays empty: nothing to search for either way.
        Assert.That(SearchTerms.CleanTitle(""), Is.Empty);
    }

    [Test]
    public void CleanTitle_DoesNotBreakWordsContainingFt()
    {
        // "Softly" must never lose its "ft" letters: the featuring tag
        // needs surrounding whitespace to count.
        Assert.That(SearchTerms.CleanTitle("Softly Rocking"), Is.EqualTo("Softly Rocking"));
    }

    [Test]
    public void Query_JoinsCleanArtistAndTitle()
        => Assert.That(SearchTerms.Query("Bén", "Für Elise (Original Mix)"),
            Is.EqualTo("Ben Fur Elise"));

    [Test]
    public void Query_EmptyPartsDoNotLeaveDoubleSpaces()
        => Assert.That(SearchTerms.Query("", "Some Title"),
            Is.EqualTo("Some Title"));
}
