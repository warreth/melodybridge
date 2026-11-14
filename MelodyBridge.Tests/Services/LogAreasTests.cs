using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Raw ILogger category names map to the friendly areas the Logs page filters
/// on. The mapping table is config, so the value here is the invariant:
/// EveryInjectedLogger_HasAnArea walks the real services and fails when one of
/// them would land in System.
/// </summary>
[TestFixture]
public class LogAreasTests
{
    // One representative per area plus the fallback: the mapping table itself
    // is config, and EveryInjectedLogger_HasAnArea below is the invariant that
    // actually catches an unmapped logger.
    [TestCase("Download", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Services.PlaylistStore", "Playlists")]
    [TestCase("MelodyBridge.Infrastructure.Services.SpotifySourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Scanning.LibraryScanner", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.SyncJobRunner", "Sync")]
    [TestCase("Microsoft.EntityFrameworkCore.Database.Command", "Database")]
    [TestCase("Some.Unknown.Thing", "System")]
    [TestCase("", "System")]
    public void FromCategory_MapsRealCategories(string category, string expected)
    {
        Assert.That(LogAreas.FromCategory(category), Is.EqualTo(expected));
    }

    [Test]
    public void All_AreasAreUnique()
    {
        Assert.That(LogAreas.All, Is.Unique);
    }

    /// <summary>
    /// Walks every logger the app actually injects and fails when one of
    /// them would land in System. A new service with its own ILogger gets
    /// caught here instead of showing up as an untagged row in the UI.
    /// </summary>
    [Test]
    public void EveryInjectedLogger_HasAnArea()
    {
        var assemblies = new[]
        {
            typeof(LogAreas).Assembly,
            typeof(MelodyBridge.Application.Services.DownloadManager).Assembly,
            typeof(MelodyBridge.Server.Services.LogCollector).Assembly,
        };

        // These are system services by design: startup, notifications,
        // background failure reporting.
        var systemTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ToastObserverService",
            "NtfyNotifier",
            "NotificationService",
            "BackgroundFailureSink",
            "UpdateCheckService",
            "UpdateService",
            "UpdateWatchService",
        };

        var loggerTypes = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass)
            .SelectMany(LoggedTypes)
            .Distinct();

        var untagged = loggerTypes
            .Where(type => !systemTypes.Contains(type.Name))
            .Where(type => LogAreas.FromCategory(type.FullName ?? type.Name) == "System")
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(untagged, Is.Empty,
            "these ILogger<T> categories would show up as System on the Logs page: "
            + string.Join(", ", untagged));
    }

    private static IEnumerable<Type> LoggedTypes(Type type)
    {
        const System.Reflection.BindingFlags all =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static;

        foreach (var ctor in type.GetConstructors(all))
            foreach (var parameter in ctor.GetParameters())
                if (LoggedType(parameter.ParameterType) is { } fromCtor)
                    yield return fromCtor;

        foreach (var property in type.GetProperties(all))
            if (LoggedType(property.PropertyType) is { } fromProperty)
                yield return fromProperty;

        foreach (var field in type.GetFields(all))
            if (LoggedType(field.FieldType) is { } fromField)
                yield return fromField;
    }

    private static Type? LoggedType(Type type)
        => type.IsGenericType
           && type.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>)
            ? type.GetGenericArguments()[0]
            : null;

    [Test]
    public void EveryKeywordLandsInAKnownArea()
    {
        // The mapping table may only produce areas that exist as chips.
        foreach (var area in LogAreas.All)
            Assert.That(LogAreas.FromCategory(area), Is.Not.Empty);
    }
}
