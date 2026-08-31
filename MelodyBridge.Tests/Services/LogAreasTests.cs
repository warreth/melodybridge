using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Raw ILogger category names must map to the friendly areas the Logs
/// page filters on. Real category names from the codebase, not made-up ones.
/// </summary>
[TestFixture]
public class LogAreasTests
{
    [TestCase("MelodyBridge.Infrastructure.Services.PlaylistStore", "Playlists")]
    [TestCase("MelodyBridge.Application.Services.DownloadManager", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.SoundCloudDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.ArchiveOrgDownloader", "Downloads")]
    [TestCase("MelodyBridge.Infrastructure.Services.SpotifySourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Services.YouTubeSourceProvider", "Sources")]
    [TestCase("MelodyBridge.Infrastructure.Scanning.LibraryScanner", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.FileSystemMonitor", "Library")]
    [TestCase("MelodyBridge.Infrastructure.Services.SyncJobRunner", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Services.AutoSyncBackgroundService", "Sync")]
    [TestCase("MelodyBridge.Infrastructure.Services.JellyfinSync", "Sync")]
    [TestCase("Microsoft.EntityFrameworkCore.Database.Command", "Database")]
    public void FromCategory_MapsRealCategories(string category, string expected)
    {
        Assert.That(LogAreas.FromCategory(category), Is.EqualTo(expected));
    }

    [Test]
    public void FromCategory_UnknownFallsBackToSystem()
    {
        Assert.That(LogAreas.FromCategory("Some.Unknown.Thing"), Is.EqualTo("System"));
        Assert.That(LogAreas.FromCategory(""), Is.EqualTo("System"));
    }

    [Test]
    public void All_AreasAreUnique()
    {
        Assert.That(LogAreas.All, Is.Unique);
    }
}
