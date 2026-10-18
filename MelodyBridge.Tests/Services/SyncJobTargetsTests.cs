using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The sync job target list: a job may publish to an M3U file and several
/// media servers at once. The JSON list is the source of truth; rows from
/// before multi-target support keep working through their legacy single
/// OutputTarget fields, and the legacy columns stay mirrored for older
/// readers.
/// </summary>
[TestFixture]
public class SyncJobTargetsTests
{
    [Test]
    public void LegacyRow_MapsToOneTarget()
    {
        var entity = new SyncJobEntity
        {
            OutputTarget = "JellyfinApi",
            MediaServerProfileId = "p1",
            M3uOutputPath = "/unused.m3u",
        };

        var targets = SyncJobMapping.TargetsOf(entity);

        Assert.That(targets, Is.EqualTo(new[]
        {
            new SyncTarget(OutputTargetType.JellyfinApi, ProfileId: "p1"),
        }));
    }

    [Test]
    public void LegacyRow_WithoutAProfile_KeepsTheInlineConnectionShape()
    {
        var entity = new SyncJobEntity
        {
            OutputTarget = "JellyfinApi",
            JellyfinServerUrl = "http://old:8096",
        };

        var targets = SyncJobMapping.TargetsOf(entity);

        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(1));
            Assert.That(targets[0].ProfileId, Is.Null,
                "a null profile is the job's own saved connection");
        });
    }

    [Test]
    public void LegacyM3uRow_KeepsItsPath()
    {
        var entity = new SyncJobEntity { OutputTarget = "M3uFile", M3uOutputPath = "/out/a.m3u" };

        var targets = SyncJobMapping.TargetsOf(entity);

        Assert.That(targets.Single().M3uPath, Is.EqualTo("/out/a.m3u"));
    }

    [Test]
    public void ApplyTargets_RoundTrips_AndMirrorsTheFirstServersAndPath()
    {
        var entity = new SyncJobEntity();

        SyncJobMapping.ApplyTargets(entity, new[]
        {
            new SyncTarget(OutputTargetType.M3uFile, M3uPath: "/out/a.m3u"),
            new SyncTarget(OutputTargetType.JellyfinApi, ProfileId: "p-jf"),
            new SyncTarget(OutputTargetType.PlexApi, ProfileId: "p-px"),
        });

        var targets = SyncJobMapping.TargetsOf(entity);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(3));
            Assert.That(targets[1].ProfileId, Is.EqualTo("p-jf"));
            Assert.That(targets[2].Kind, Is.EqualTo(OutputTargetType.PlexApi));
            Assert.That(entity.OutputTarget, Is.EqualTo("M3uFile"),
                "the legacy column mirrors the first target");
            Assert.That(entity.M3uOutputPath, Is.EqualTo("/out/a.m3u"));
            Assert.That(entity.MediaServerProfileId, Is.EqualTo("p-jf"),
                "the legacy column mirrors the first server profile");
        });
    }

    [Test]
    public void ApplyTargets_DropsPathlessM3uAndDuplicates()
    {
        var entity = new SyncJobEntity();

        SyncJobMapping.ApplyTargets(entity, new[]
        {
            new SyncTarget(OutputTargetType.M3uFile),
            new SyncTarget(OutputTargetType.PlexApi, ProfileId: "p"),
            new SyncTarget(OutputTargetType.PlexApi, ProfileId: "p"),
        });

        var targets = SyncJobMapping.TargetsOf(entity);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(1), "an M3U target without a path cannot run");
            Assert.That(targets[0].Kind, Is.EqualTo(OutputTargetType.PlexApi));
        });
    }

    [Test]
    public void DamagedTargetsJson_FallsBackToTheLegacyFields()
    {
        var entity = new SyncJobEntity
        {
            TargetsJson = "{not json at all",
            OutputTarget = "PlexApi",
            MediaServerProfileId = "p2",
        };

        var targets = SyncJobMapping.TargetsOf(entity);

        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(1));
            Assert.That(targets[0].Kind, Is.EqualTo(OutputTargetType.PlexApi));
            Assert.That(targets[0].ProfileId, Is.EqualTo("p2"));
        });
    }

    [Test]
    public void MapToDomain_CarriesEveryTarget()
    {
        var entity = new SyncJobEntity();
        SyncJobMapping.ApplyTargets(entity, new[]
        {
            new SyncTarget(OutputTargetType.SubsonicApi, ProfileId: "p-nav"),
            new SyncTarget(OutputTargetType.M3uFile, M3uPath: "/out/nav.m3u"),
        });

        var job = SyncJobMapping.MapToDomain(entity);

        Assert.That(job.Targets.Select(t => t.Kind),
            Is.EqualTo(new[] { OutputTargetType.SubsonicApi, OutputTargetType.M3uFile }));
    }
}
