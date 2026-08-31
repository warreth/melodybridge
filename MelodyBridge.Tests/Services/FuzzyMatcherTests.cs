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
}
