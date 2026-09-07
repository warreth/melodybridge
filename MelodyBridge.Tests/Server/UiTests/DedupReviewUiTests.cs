using Bunit;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The needs-review state in TrackTable: the warning pill, the two
/// resolution buttons (download a new file / hardlink the existing one)
/// with their exact callback payloads, and that no other download action
/// is offered while a track is paused for review.
/// </summary>
[TestFixture]
[Category("UI")]
public class DedupReviewUiTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new Bunit.TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private static TrackEntity ReviewTrack() => new()
    {
        Title = "Shared Song",
        Artist = "Artist",
        Position = 0,
        DownloadStatus = "needs-review",
        Warning = "dedup conflict: playlist A has this as mp3 320 kbps, this playlist wants flac",
    };

    /// <summary>Renders one track, capturing every OnResolveReview payload.</summary>
    private (IRenderedComponent<TrackTable> Cut, List<(TrackEntity Track, bool HardlinkExisting)> Fired) Render(
        TrackEntity track)
    {
        var fired = new List<(TrackEntity Track, bool HardlinkExisting)>();
        var cut = _ctx.Render<TrackTable>(pb =>
        {
            pb.Add(t => t.Tracks, new List<TrackEntity> { track });
            pb.Add(t => t.OnResolveReview, choice =>
            {
                fired.Add(choice);
                return Task.CompletedTask;
            });
        });
        return (cut, fired);
    }

    [Test]
    public void NeedsReview_Shows_Warn_Pill_WithReviewText()
    {
        var (cut, _) = Render(ReviewTrack());

        var row = cut.Find("tbody tr");
        Assert.That(row.TextContent, Does.Contain("needs review"),
            "the pill must say the track waits for a human");
        Assert.That(row.InnerHtml, Does.Contain("warn"),
            "the pill uses the warning variant, not the error one");
        // The conflict reason surfaces as the pill's tooltip.
        Assert.That(row.InnerHtml, Does.Contain("dedup conflict"),
            "the quality difference must be discoverable from the row");
    }

    [Test]
    public void NeedsReview_Offers_Both_Resolution_Buttons()
    {
        var (cut, _) = Render(ReviewTrack());

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.That(buttons, Does.Contain("Download new"),
            "the exact 'Download new' action must be offered");
        Assert.That(buttons, Does.Contain("Hardlink existing"),
            "the exact 'Hardlink existing' action must be offered");
    }

    [Test]
    public void DownloadNewButton_Payload_HardlinkFalse()
    {
        var (cut, fired) = Render(ReviewTrack());

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Download new").Click();

        Assert.That(fired, Has.Count.EqualTo(1));
        Assert.That(fired[0].HardlinkExisting, Is.False,
            "false tells the store to download a fresh file");
        Assert.That(fired[0].Track.DownloadStatus, Is.EqualTo("needs-review"));
    }

    [Test]
    public void HardlinkExistingButton_Payload_HardlinkTrue()
    {
        var (cut, fired) = Render(ReviewTrack());

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Hardlink existing").Click();

        Assert.That(fired, Has.Count.EqualTo(1));
        Assert.That(fired[0].HardlinkExisting, Is.True,
            "true tells the store to link the other playlist's file");
    }

    [Test]
    public void NeedsReview_HidesThePlainDownloadButton()
    {
        var (cut, _) = Render(ReviewTrack());

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.That(buttons, Does.Not.Contain("Download"),
            "the plain single-track download is replaced by the two review actions");
    }

    [Test]
    public void DownloadedTrack_KeepsItsUsualActions()
    {
        var (cut, _) = Render(new TrackEntity
        {
            Title = "Plain", Artist = "A", Position = 0,
            DownloadStatus = "downloaded", CurrentPath = "/x/a.mp3",
        });

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.That(buttons, Does.Not.Contain("Download new"));
        Assert.That(buttons, Does.Not.Contain("Hardlink existing"));
    }
}
