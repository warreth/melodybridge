using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The flagged quality review in TrackTable: the warn pill narrates the
/// doubt, the row offers exactly Keep and Retry (no plain Download), both
/// callbacks carry the right action, and a track with only an
/// informational warning (a quality hint) gets no decision buttons.
/// </summary>
[TestFixture]
[Category("UI")]
public class QualityReviewUiTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new Bunit.TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private static TrackEntity ReviewedTrack() => new()
    {
        Title = "Wrong Length Song",
        Artist = "Artist",
        Position = 0,
        DownloadStatus = "downloaded",
        CurrentPath = "/music/wrong-length.mp3",
        ReviewKind = TrackReview.Length,
        ReviewText = "duration mismatch: 345.2s vs expected 300s",
        SourcePluginId = "yt-dlp",
    };

    /// <summary>Renders one track, capturing every OnQualityReview payload.</summary>
    private (IRenderedComponent<TrackTable> Cut, List<(TrackEntity Track, QualityReviewAction Action)> Fired) Render(
        TrackEntity track)
    {
        var fired = new List<(TrackEntity Track, QualityReviewAction Action)>();
        var cut = _ctx.Render<TrackTable>(pb =>
        {
            pb.Add(t => t.Tracks, new List<TrackEntity> { track });
            pb.Add(t => t.OnQualityReview, choice =>
            {
                fired.Add(choice);
                return Task.CompletedTask;
            });
        });
        return (cut, fired);
    }

    [Test]
    public void ReviewedTrack_ShowsKeepAndRetry_InsteadOfDownload()
    {
        var (cut, _) = Render(ReviewedTrack());

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(buttons, Does.Contain("Keep"), "the user can accept the file");
            Assert.That(buttons, Does.Contain("Retry"), "the user can ask for another provider");
            Assert.That(buttons, Does.Not.Contain("Download"), "the plain download is replaced by the decision");
            Assert.That(buttons, Does.Contain("Remove"), "Remove stays available");
        });
    }

    [Test]
    public void ReviewedTrack_PillNarratesTheDoubt()
    {
        var (cut, _) = Render(ReviewedTrack());

        var row = cut.Find("tbody tr");
        Assert.That(row.InnerHtml, Does.Contain("warn"), "the review shows the warning pill");
        Assert.That(row.InnerHtml, Does.Contain("duration mismatch"),
            "hovering the pill explains why the file was flagged");
    }

    [Test]
    public void KeepButton_CarriesTheKeepAction()
    {
        var (cut, fired) = Render(ReviewedTrack());

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Keep").Click();

        Assert.That(fired, Has.Count.EqualTo(1));
        Assert.That(fired[0].Action, Is.EqualTo(QualityReviewAction.Keep));
        Assert.That(fired[0].Track.Title, Is.EqualTo("Wrong Length Song"));
    }

    [Test]
    public void RetryButton_CarriesTheRetryAction_AndMentionsTheProvider()
    {
        var (cut, fired) = Render(ReviewedTrack());

        var retry = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Retry");
        Assert.That(retry.GetAttribute("title"), Does.Contain("provider"),
            "the tooltip says the retry avoids the delivering provider");
        retry.Click();

        Assert.That(fired, Has.Count.EqualTo(1));
        Assert.That(fired[0].Action, Is.EqualTo(QualityReviewAction.Retry));
    }

    [Test]
    public void RetryWithoutKnownProvider_StillWorks_WithHonestTooltip()
    {
        var track = ReviewedTrack();
        track.SourcePluginId = null;
        var (cut, fired) = Render(track);

        var retry = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Retry");
        Assert.That(retry.GetAttribute("title"), Does.Contain("better source"),
            "without a remembered provider the tooltip does not promise provider avoidance");
        retry.Click();
        Assert.That(fired.Single().Action, Is.EqualTo(QualityReviewAction.Retry));
    }

    [Test]
    public void InformationalWarning_ShowsPill_ButNoDecisionButtons()
    {
        // A "quality hint" is advisory only: nothing to decide.
        var track = new TrackEntity
        {
            Title = "Low Bandwidth",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "downloaded",
            CurrentPath = "/music/hint.mp3",
            Warning = "quality hint: bandwidth drops off at 15.0 kHz, about 160 kbps of real detail",
        };

        var (cut, _) = Render(track);

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.That(buttons, Does.Not.Contain("Keep"));
        Assert.That(buttons, Does.Not.Contain("Retry"));
        Assert.That(cut.Find("tbody tr").InnerHtml, Does.Contain("quality hint"),
            "the advisory warning still shows as the pill tooltip");
    }

    [Test]
    public void ReviewAndWarning_Together_ReadAsOnePillTooltip()
    {
        var track = ReviewedTrack();
        track.Warning = "quality hint: bandwidth drops off at 15.0 kHz";

        var (cut, _) = Render(track);

        var pills = cut.FindAll("span.pill.warn");
        Assert.That(pills, Has.Count.EqualTo(1), "one pill, not a stack");
        var title = pills[0].GetAttribute("title");
        Assert.That(title, Does.Contain("duration mismatch"));
        Assert.That(title, Does.Contain("quality hint"));
    }

    [Test]
    public void PendingTrack_KeepsThePlainDownloadButton()
    {
        var track = new TrackEntity
        {
            Title = "Not Yet",
            Artist = "Artist",
            Position = 0,
            DownloadStatus = "pending",
        };

        var (cut, _) = Render(track);

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.That(buttons, Does.Contain("Download"));
        Assert.That(buttons, Does.Not.Contain("Keep"));
        Assert.That(buttons, Does.Not.Contain("Retry"));
    }
}
