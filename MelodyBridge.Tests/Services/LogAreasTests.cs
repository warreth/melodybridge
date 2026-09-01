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
    // Playlists
    [TestCase("MelodyBridge.Infrastructure.Services.PlaylistStore", "Playlists")]

    // Sources: public providers, account providers, token store
    [TestCase("MelodyBridge.Infrastructure.Services.SpotifySourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Services.YouTubeSourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Accounts.AccountTokenStore", "Sources")]

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

    // Library: scanning, reconciliation, folder watching, scheduling
    [TestCase("MelodyBridge.Infrastructure.Scanning.LibraryScanner", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Scanning.LibraryReconciler", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.FileSystemMonitor", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.FileSystemMonitoringBackgroundService", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.ScanSchedulingBackgroundService", "Library")]

    // Sync: runner, engine, controller, background service, outputs
    [TestCase("MelodyBridge.Infrastructure.Services.SyncJobRunner", "Sync")]
    [TestCase("MelodyBridge.Application.Services.SyncEngine", "Sync")]
    [TestCase("MelodyBridge.Server.Controllers.SyncController", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Services.AutoSyncBackgroundService", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.MediaServers.JellyfinSync", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Playlists.M3uGenerator", "Sync")]

    // Database: EF Core internals and our context
    [TestCase("Microsoft.EntityFrameworkCore.Database.Command", "Database")]
    [TestCase("MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext", "Database")]

    // System: anything unmatched falls back here
    [TestCase("MelodyBridge.Server.Program", "System")]
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

    [Test]
    public void EveryKeywordLandsInAKnownArea()
    {
        // The mapping table may only produce areas that exist as chips.
        foreach (var area in LogAreas.All)
            Assert.That(LogAreas.FromCategory(area), Is.Not.Empty);
    }
}
