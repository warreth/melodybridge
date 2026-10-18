using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// Candidate ranking: exact matches beat remixes, duration proximity
/// breaks ties between identical titles, and grossly wrong lengths are
/// dropped so a preview or extended edit never becomes the download.
/// </summary>
[TestFixture]
public class SearchRankerTests
{
    private static DownloaderSearchHit Hit(
        string title, string artist, string url,
        double? seconds = null, MatchConfidence confidence = MatchConfidence.High)
        => new(title, artist, url,
            seconds is null ? null : TimeSpan.FromSeconds(seconds.Value),
            MatchConfidence: confidence);

    [Test]
    public void ExactTitle_BeatsTheRemix_ListedFirst()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[]
            {
                Hit("Never Gonna Give You Up (Remix)", "Rick Astley", "https://x/1", 180),
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/2", 213),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/2"));
    }

    [Test]
    public void DurationProximity_BreaksTheTieBetweenIdenticalTitles()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", TimeSpan.FromSeconds(213),
            new[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/live", 400),
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/studio", 214),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/studio"),
            "the length closest to the playlist's duration must win");
    }

    [Test]
    public void GrossDurationMismatch_LosesAgainstAPlausibleCandidate()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", TimeSpan.FromSeconds(213),
            new[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/snippet", 30),
                Hit("Never Gonna Give You Up (Official Audio)", "Rick Astley", "https://x/full", 214),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/full"));
    }

    [Test]
    public void OnlyImplausibleCandidates_SurviveAsALastResort()
    {
        // The waterfall's own duration gate rejects this before any
        // download; returning it keeps the failure reason precise
        // ("wrong length") instead of a bare "no source had this track".
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", TimeSpan.FromSeconds(213),
            new[] { Hit("Never Gonna Give You Up", "Rick Astley", "https://x/snippet", 30) });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/snippet"));
        Assert.That(result.Alternatives, Is.Null, "a wrong-length hit is never offered as a fallback");
    }

    [Test]
    public void UnknownDurations_AreNeverFiltered()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", TimeSpan.FromSeconds(213),
            new[] { Hit("Never Gonna Give You Up", "Rick Astley", "https://x/unknown") });

        Assert.That(result, Is.Not.Null, "no duration data means no verdict");
    }

    [Test]
    public void Alternatives_AreAttached_InRankOrder()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/1"),
                Hit("Never Gonna Give You Up (Radio Edit)", "Rick Astley", "https://x/2"),
                Hit("Never Gonna Give You Up (Live)", "Rick Astley", "https://x/3"),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/1"));
        // The live cut adds an unrequested modifier and is dropped, so
        // only the radio edit remains as a runner-up.
        Assert.That(result.Alternatives, Has.Count.EqualTo(1));
        Assert.That(result.Alternatives![0].SourceUrl, Is.EqualTo("https://x/2"));
    }

    [Test]
    public void TieBreaker_BreaksEqualScores()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/lossy"),
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/lossless"),
            },
            tieBreaker: hit => hit.SourceUrl == "https://x/lossless" ? 1 : 0);

        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/lossless"));
    }

    [Test]
    public void LowConfidenceRunnerUps_AreNotOfferedAsAlternatives()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[]
            {
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/1"),
                Hit("Never Gonna Give You Up (Karaoke)", "Some Cover Band", "https://x/2",
                    confidence: MatchConfidence.Low),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Alternatives, Is.Null,
            "a Low-confidence cover is the wrong track, not a fallback");
    }

    [Test]
    public void UnrequestedModifiers_LoseToCleanCandidates()
    {
        // The karaoke version is listed first; the clean match must win.
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[]
            {
                Hit("Never Gonna Give You Up (Karaoke Version)", "Karaoke Channel", "https://x/karaoke"),
                Hit("Never Gonna Give You Up", "Rick Astley", "https://x/real"),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/real"));
    }

    [Test]
    public void KaraokeOnly_ReturnsNoHit()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[] { Hit("Never Gonna Give You Up (Karaoke Version)", "Karaoke Channel", "https://x/karaoke") });

        Assert.That(result, Is.Null,
            "a karaoke recording is the wrong track, never a fallback");
    }

    [Test]
    public void SharedWordsWithAWrongArtist_ReturnNoHit()
    {
        // The reported case: "SLVL - The Last Airbender (VIP)" must never
        // resolve to an Avatar soundtrack upload that shares two words.
        var result = SearchRanker.Rank("SLVL", "The Last Airbender (VIP)", null,
            new[]
            {
                Hit("Avatar: The Last Airbender Book 1 Water", "Avatar", "https://x/avatar",
                    300, MatchConfidence.Low),
                Hit("The Last Airbender Review", "Some Reviewer", "https://x/review",
                    600, MatchConfidence.Low),
            });

        Assert.That(result, Is.Null,
            "a wrong song is worse than a miss; the manual picker is the fallback");
    }

    [Test]
    public void HitThatDropsARequestedVersionMarker_ReturnNoHit()
    {
        // A soundtrack track is actually titled "The Last Airbender": the
        // words match, the recording does not.
        var result = SearchRanker.Rank("SLVL", "The Last Airbender (VIP)", null,
            new[] { Hit("The Last Airbender", "Avatar", "https://x/avatar", 217) });

        Assert.That(result, Is.Null, "the (VIP) marker is the difference between the two songs");
    }

    [Test]
    public void HitThatKeepsTheVersionMarker_IsAccepted()
    {
        var result = SearchRanker.Rank("SLVL", "The Last Airbender (VIP)", null,
            new[] { Hit("SLVL - The Last Airbender (VIP)", "SLVL", "https://x/real", 217) });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/real"));
    }

    [Test]
    public void ExactTitle_FromAnUnknownUploadChannel_IsAccepted()
    {
        // Upload platforms prefix the artist and channel names rarely
        // match the credit: the exact title is the signal that matters.
        var result = SearchRanker.Rank("SLVL", "The Last Airbender (VIP)", null,
            new[]
            {
                Hit("SLVL - The Last Airbender (VIP)", "Some Upload Channel", "https://x/real", 210),
            });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SourceUrl, Is.EqualTo("https://x/real"));
    }

    [Test]
    public void StrongArtistMatch_WithPartOfTheTitle_IsAccepted()
    {
        var result = SearchRanker.Rank("Rick Astley", "Never Gonna Give You Up", null,
            new[] { Hit("Never Gonna", "Rick Astley", "https://x/short") });

        Assert.That(result, Is.Not.Null,
            "the right artist with half the title is a spelling difference, not a miss");
    }

    [Test]
    public void NoCandidates_ReturnsNull()
        => Assert.That(SearchRanker.Rank("A", "T", null, Array.Empty<DownloaderSearchHit>()), Is.Null);
}
