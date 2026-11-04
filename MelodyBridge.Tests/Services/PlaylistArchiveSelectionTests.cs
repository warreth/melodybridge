using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class PlaylistArchiveSelectionTests
{
    [Test]
    public void NullSelection_IsLegacy()
    {
        Assert.That(PlaylistArchiveSelection.Parse(null), Is.Null);
        Assert.That(PlaylistArchiveSelection.Parse(""), Is.Null);
        Assert.That(PlaylistArchiveSelection.IncludesDefault(null, legacyEnabled: true), Is.True);
        Assert.That(PlaylistArchiveSelection.IncludesDefault(null, legacyEnabled: false), Is.False);
    }

    [Test]
    public void ExplicitSelection_RoundTripsAndKeepsDefault()
    {
        var json = PlaylistArchiveSelection.Serialize(
            new[] { "p-opus", PlaylistArchiveSelection.DefaultId, "p-opus" });

        Assert.That(PlaylistArchiveSelection.Parse(json),
            Is.EqualTo(new[] { PlaylistArchiveSelection.DefaultId, "p-opus" }));
        Assert.That(PlaylistArchiveSelection.IncludesDefault(json, legacyEnabled: false), Is.True);
    }

    [Test]
    public void EmptySelection_MeansNoArchives()
    {
        var json = PlaylistArchiveSelection.Serialize(Array.Empty<string>());
        Assert.That(PlaylistArchiveSelection.Parse(json), Is.Empty);
        Assert.That(PlaylistArchiveSelection.IncludesDefault(json, legacyEnabled: true), Is.False);
    }

    [Test]
    public void CorruptSelection_FallsBackToLegacyInsteadOfDroppingCopies()
    {
        Assert.That(PlaylistArchiveSelection.Parse("{not json"), Is.Null);
        Assert.That(PlaylistArchiveSelection.IncludesDefault("{not json", legacyEnabled: true), Is.True);
    }
}
