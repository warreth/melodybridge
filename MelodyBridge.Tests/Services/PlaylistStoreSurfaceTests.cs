using System.Reflection;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Pins the public surface of PlaylistStore after the split: the pages,
/// the coordinator and the background services call these members, and the
/// facade must keep them even as the implementations move into their own
/// services. A rename or removal fails here first.
/// </summary>
[TestFixture]
public class PlaylistStoreSurfaceTests
{
    private static readonly string[] InstanceMethods =
    {
        // Download pipeline
        "DownloadMissingAsync", "DownloadTrackAsync", "FindDedupCandidateAsync", "ResolveReviewAsync", "ResolveQualityReviewAsync",
        // Queries, lifecycle and maintenance
        "GetAllAsync", "GetDownloadStatsAsync", "GetSavedExternalIdsAsync", "RemoveTrackAsync",
        "GetByIdAsync", "DeleteAsync", "UpdateSettingsAsync", "UpdateScheduleAsync",
        "GetDueForAutoSyncAsync", "RecomputeMissingFactsAsync", "RetagLibraryAsync",
        // Sources and snapshots
        "AddOrRefreshAsync", "AddOrRefreshDetailedAsync", "AddOrRefreshLikedAsync",
        "ImportFileAsync", "RefreshAsync",
        // Transfer
        "ExportAsync", "ExportCsvAsync", "ImportAsync",
    };

    [Test]
    public void Facade_KeepsEveryMethodTheAppCalls()
    {
        var methods = typeof(PlaylistStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = InstanceMethods.Where(name => !methods.Contains(name)).ToList();
        Assert.That(missing, Is.Empty,
            "the split must not change what callers can call");
    }

    [Test]
    public void Facade_KeepsTheStaticsAndTheConstructorShape()
    {
        var type = typeof(PlaylistStore);

        Assert.Multiple(() =>
        {
            Assert.That(type.GetMethod("ParseQuality", BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null,
                "the tests and the pages parse quality through the store");
            Assert.That(type.GetMethod("IsValidFormat", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(type.GetField("FormatOptions", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(type.GetField("DedupSettingKey", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
            Assert.That(type.GetProperty("Verification", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);

            var ctor = type.GetConstructors().Single();
            var parameters = ctor.GetParameters().Select(p => p.ParameterType.Name).ToList();
            Assert.That(parameters.Take(4), Is.EqualTo(new[]
            {
                "IDbContextFactory`1", "IEnumerable`1", "IDownloadManager", "ILogger`1",
            }), "the first four constructor parameters stay positional for the test fixtures");
            Assert.That(parameters, Does.Contain("CoverArtFetcher"));
            Assert.That(parameters, Does.Contain("LyricsResolver"));
        });
    }

    [Test]
    public void Facade_IsDelegationOnly()
    {
        // The point of the split: the facade must stay small. If it grows
        // again, logic has crept back in.
        var text = File.ReadAllText(SourceFile());
        var lines = text.Split('\n').Length;
        Assert.That(lines, Is.LessThan(260),
            $"PlaylistStore should stay a thin facade; it is {lines} lines");
    }

    /// <summary>The source file, found from the repo root the tests run in.</summary>
    private static string SourceFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MelodyBridge.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "repository root not found");
        return Path.Combine(dir!.FullName,
            "MelodyBridge.Infrastructure", "Services", "PlaylistStore.cs");
    }
}
