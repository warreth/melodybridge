using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Raw ILogger category names must map to the friendly areas the Logs
/// page filters on. One TestCase per real logger the app creates: when a
/// new logger is added without a mapping it lands in "System", and the
/// case below is the reminder to give it a home.
/// </summary>
[TestFixture]
public class LogAreasTests
{
    // Dev panel short categories (no namespace): these used to land in System
    [TestCase("Download", "Downloads")]
    [TestCase("Queue", "Downloads")]
    [TestCase("Search", "Sources")]
    [TestCase("Sync", "Sync")]
    [TestCase("Scanner", "Library")]

    // Playlists
    [TestCase("MelodyBridge.Infrastructure.Services.PlaylistStore", "Playlists")]
    [TestCase("MelodyBridge.Infrastructure.Playlists.PlaylistTransfer", "Playlists")]

    // Sources: public providers, account providers, token store
    [TestCase("MelodyBridge.Infrastructure.Services.SpotifySourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Services.YouTubeSourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Accounts.AccountTokenStore", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Services.IsrcResolver", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Services.ListenBrainzSourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Services.DeezerSourceProvider", "Sources")]

    // Downloads: manager, coordinator, registry, every plugin, probes
    [TestCase("MelodyBridge.Application.Services.DownloadManager", "Downloads")]
    [TestCase("MelodyBridge.Application.Services.DownloadCoordinator", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Services.DownloaderRegistry", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.SoundCloudDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.ArchiveOrgDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.YtDlpDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Lucida.LucidaDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Audio.BitrateProbe", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Qobuz.QobuzClient", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Tidal.TidalClient", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Deezer.DeezerClient", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.MonochromeDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.SlskdDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Jumo.JumoClient", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Jumo.JumoDownloader", "Downloads")]

    // Library: scanning, reconciliation, folder watching, scheduling
    [TestCase("MelodyBridge.Infrastructure.Scanning.LibraryScanner", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Scanning.LibraryReconciler", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.FileSystemMonitor", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.FileSystemMonitoringBackgroundService", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.ScanSchedulingBackgroundService", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.TrackLinker", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.LibraryPathMover", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.CoverArtFetcher", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.LyricsResolver", "Library")]

    // Sync: runner, engine, controller, background service, outputs
    [TestCase("MelodyBridge.Infrastructure.Services.SyncJobRunner", "Sync")]
    [TestCase("MelodyBridge.Application.Services.SyncEngine", "Sync")]
    [TestCase("MelodyBridge.Server.Controllers.SyncController", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Services.AutoSyncBackgroundService", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.MediaServers.JellyfinSync", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.MediaServers.PlexSync", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.MediaServers.SubsonicSync", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Playlists.M3uGenerator", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Services.SyncJobSchedulingBackgroundService", "Sync")]

    // Database: EF Core internals and our context
    [TestCase("Microsoft.EntityFrameworkCore.Database.Command", "Database")]
    [TestCase("MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext", "Database")]
    [TestCase("MelodyBridge.Infrastructure.Services.DatabaseBackupService", "Database")]

    // System: startup, notifications and anything unmatched
    [TestCase("MelodyBridge.Server.Program", "System")]
    [TestCase("MelodyBridge.Server.Services.ToastObserverService", "System")]
    [TestCase("MelodyBridge.Server.Services.NtfyNotifier", "System")]
    [TestCase("MelodyBridge.Server.Services.NotificationService", "System")]
    [TestCase("MelodyBridge.Server.Services.BackgroundFailureSink", "System")]
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
        // background failure reporting and the dev panel itself.
        var systemTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ToastObserverService",
            "NtfyNotifier",
            "NotificationService",
            "BackgroundFailureSink",
            "UpdateCheckService",
            "DevPanelService",
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
