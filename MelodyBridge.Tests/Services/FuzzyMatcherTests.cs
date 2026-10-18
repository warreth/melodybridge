using MelodyBridge.Core;
using MatchConfidence = MelodyBridge.Core.MatchConfidence;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Fuzzy matcher against real-world messy metadata: featuring credits,
/// live markers, casing, accents, remix tags.
/// </summary>
[TestFixture]
public class FuzzyMatcherTests
{
    [TestCase("Rick Astley", "Never Gonna Give You Up",
              "Rick Astley", "Never Gonna Give You Up",
              MatchConfidence.High)]
    [TestCase("Ludwig van Beethoven", "Für Elise",
              "Ludwig van Beethoven", "Fur Elise",
              MatchConfidence.High)] // accent-stripped
    [TestCase("The Chainsmokers", "Don't Let Me Down",
              "The Chainsmokers ft. Daya", "Don't Let Me Down (Illenium Remix)",
              MatchConfidence.High)] // featuring + remix suffix
    [TestCase("Daft Punk", "One More Time",
              "Daft Punk", "ONE MORE TIME!! (Official Audio)",
              MatchConfidence.High)] // casing, punctuation, official audio
    [TestCase("Artist A", "Song X",
              "Completely Different Artist", "A Totally Different Song",
              MatchConfidence.Low)]
    [TestCase("Artist A", "Song X",
              "Artist A", "Song X Live at Wembley 2014 Remastered",
              MatchConfidence.Low)] // live markers: not the studio original
    public void Confidence_RealWorldPairs(
        string reqArtist, string reqTitle, string hitArtist, string hitTitle,
        MatchConfidence expected)
    {
        Assert.That(
            FuzzyMatcher.Confidence(reqArtist, reqTitle, hitArtist, hitTitle),
            Is.EqualTo(expected),
            $"{reqArtist} - {reqTitle} vs {hitArtist} - {hitTitle}");
    }

    [Test]
    public void Similarity_EmptyInputs_ScoreZero()
    {
        Assert.That(FuzzyMatcher.Similarity("", "song"), Is.EqualTo(0));
        Assert.That(FuzzyMatcher.Similarity("song", null), Is.EqualTo(0));
        Assert.That(FuzzyMatcher.Similarity("", ""), Is.EqualTo(0));
    }

    [Test]
    public void Similarity_TokenOrder_Ignored()
    {
        Assert.That(
            FuzzyMatcher.Similarity("Song Title Here", "here title song"),
            Is.GreaterThan(0.9));
    }

    [Test]
    public void Similarity_StopWords_Ignored()
    {
        // "the" and "official video" must not lower the score of an identical title.
        Assert.That(
            FuzzyMatcher.Similarity("The Song", "Song (Official Video)"),
            Is.GreaterThan(0.9));
    }

    [TestCase("Never Gonna Give You Up", "Never Gonna Give You Up (Karaoke Version)", true)]
    [TestCase("Never Gonna Give You Up", "Never Gonna Give You Up (Live at Wembley)", true)]
    [TestCase("Never Gonna Give You Up", "Never Gonna Give You Up (Tiesto Remix)", true)]
    [TestCase("Never Gonna Give You Up", "Never Gonna Give You Up (Piano Cover)", true)]
    [TestCase("Never Gonna Give You Up", "Never Gonna Give You Up", false)]
    // The requested title itself mentions the modifier: it is the track.
    [TestCase("Song (Live)", "Song (Live at Wembley)", false)]
    [TestCase("Cover Me", "Cover Me (Official Audio)", false)]
    // Token boundaries: "Alive" is not "live".
    [TestCase("Stayin' Alive", "Stayin' Alive", false)]
    // Promo content is not the song.
    [TestCase("The Last Airbender", "The Last Airbender Review", true)]
    [TestCase("Song", "Song (Reaction)", true)]
    public void AddsUnrequestedModifier_FlagsOnlyNewModifiers(
        string requested, string hit, bool expected)
    {
        Assert.That(FuzzyMatcher.AddsUnrequestedModifier(requested, hit), Is.EqualTo(expected),
            $"{requested} vs {hit}");
    }

    [TestCase("The Last Airbender (VIP)", "The Last Airbender", true)]
    [TestCase("The Last Airbender (VIP)", "SLVL - The Last Airbender (VIP)", false)]
    [TestCase("Song (Acoustic)", "Song", true)]
    [TestCase("Song (Acoustic)", "Song (Acoustic Version)", false)]
    [TestCase("Song (Live)", "Song (Live at Wembley)", false)]
    [TestCase("Song", "Song (Live)", false)]
    // Marker spellings: platforms write both for the same thing.
    [TestCase("Song (Remastered)", "Artist - Song (Remaster)", false)]
    [TestCase("Song (Remaster)", "Artist - Song (Remastered)", false)]
    [TestCase("Song (Remixes)", "Song (Remix)", false)]
    // Promotional parentheses are not version markers; the search text
    // drops them on purpose, so a plain hit must stay acceptable.
    [TestCase("Nightshift (Original Mix)", "Nightshift", false)]
    [TestCase("Nightshift [Radio Edit]", "Nightshift", false)]
    // A vocal hook in parentheses is part of the title, not a marker.
    [TestCase("Around the World (La La La La La)", "Around the World", false)]
    public void DropsRequestedMarker_FlagsLostVersions(
        string requested, string hit, bool expected)
    {
        Assert.That(FuzzyMatcher.DropsRequestedMarker(requested, hit), Is.EqualTo(expected),
            $"{requested} vs {hit}");
    }

    [TestCase("Ludwig van Beethoven", "Für Elise", "Rousseau", "Beethoven - Für Elise", true)]
    [TestCase("Frédéric Chopin", "Nocturne in E-flat major, Op. 9 No. 2",
        "Traum", "Chopin - Nocturne Op. 9 No. 2", true)]
    [TestCase("SLVL", "The Last Airbender", "Avatar", "Avatar: The Last Airbender Book 1 Water", false)]
    [TestCase("SLVL", "The Last Airbender", "Trailer Music", "Last Airbender Trailer Music", false)]
    // Featured credits and artist-prefixed titles are the norm on upload
    // platforms; they must not turn a working download into a miss.
    [TestCase("Sam Smith", "Unholy", "Sam Smith",
        "Sam Smith, Kim Petras - Unholy (Official Music Video)", true)]
    [TestCase("Sam Smith, Kim Petras", "Unholy (feat. Kim Petras)", "Sam Smith",
        "Sam Smith, Kim Petras - Unholy (Official Music Video)", true)]
    // The search deliberately stripped the promotional suffix: the hit it
    // asked for must pass the gate that scores the same way.
    [TestCase("The Artist", "Song (Original Mix)", "The Artist", "The Artist - Song", true)]
    [TestCase("The Artist", "Song [Official Video]", "The Artist", "The Artist - Song", true)]
    // The (VIP) case is rejected by DropsRequestedMarker, not by this gate:
    // see the test above.
    public void IsAcceptableMatch_KeepsRealHits_DropsUnrelatedTitles(
        string artist, string title, string hitArtist, string hitTitle, bool expected)
    {
        Assert.That(FuzzyMatcher.IsAcceptableMatch(artist, title, hitArtist, hitTitle),
            Is.EqualTo(expected), $"{artist} - {title} vs {hitArtist} - {hitTitle}");
    }
}
