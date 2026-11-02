using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The automatic repair mapping: each attention kind has the repair that
/// actually resolves it, not one generic retry.
/// </summary>
[TestFixture]
public class AttentionFixPolicyTests
{
    [TestCase("failed-no-source", AttentionFixAction.Retry)]
    [TestCase("failed-auth", AttentionFixAction.Retry)]
    [TestCase("dedup", AttentionFixAction.Hardlink)]
    [TestCase("review-length", AttentionFixAction.Keep)]
    [TestCase("review-spectrum", AttentionFixAction.RetryReview)]
    [TestCase("review-both", AttentionFixAction.RetryReview)]
    [TestCase("archive", AttentionFixAction.Archive)]
    [TestCase("file-missing", AttentionFixAction.Retry)]
    [TestCase("unverified", AttentionFixAction.Reverify)]
    public void EachKind_GetsItsOwnRepair(string kind, AttentionFixAction expected)
        => Assert.That(AttentionFixPolicy.ForKind(kind), Is.EqualTo(expected));

    [Test]
    public void Summary_NamesTheRepairAndTheCount()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AttentionFixPolicy.Summary(AttentionFixAction.Retry, 3),
                Is.EqualTo("retry 3 downloads"));
            Assert.That(AttentionFixPolicy.Summary(AttentionFixAction.Keep, 1),
                Is.EqualTo("keep 1 length-reviewed file"));
            Assert.That(AttentionFixPolicy.Summary(AttentionFixAction.Hardlink, 2),
                Is.EqualTo("hardlink 2 duplicates"));
            Assert.That(AttentionFixPolicy.Summary(AttentionFixAction.Archive, 1),
                Is.EqualTo("rebuild 1 archive copy"));
            Assert.That(AttentionFixPolicy.Summary(AttentionFixAction.Reverify, 2),
                Is.EqualTo("recheck 2 unverified files"));
        });
    }
}
