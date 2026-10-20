using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The Logs page source filter maps a logger category to the plugin or
/// subsystem that produced it, without guessing from the message text.
/// </summary>
[TestFixture]
public class LogSourcesTests
{
    [TestCase("MelodyBridge.Infrastructure.Qobuz.QobuzClient", "Qobuz")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.QobuzDownloader", "Qobuz")]
    [TestCase("MelodyBridge.Infrastructure.Tidal.TidalClient", "Tidal")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.TidalDownloader", "Tidal")]
    [TestCase("MelodyBridge.Infrastructure.Deezer.DeezerClient", "Deezer")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.DeemixDownloader", "Deezer")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.YtDlpDownloader", "yt-dlp")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.SoundCloudDownloader", "SoundCloud")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.MonochromeDownloader", "Monochrome")]
    [TestCase("MelodyBridge.Infrastructure.Jumo.JumoClient", "Jumo")]
    [TestCase("MelodyBridge.Infrastructure.Lucida.LucidaDownloader", "Lucida")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.ArchiveOrgDownloader", "Archive.org")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.SlskdDownloader", "Soulseek")]
    [TestCase("MelodyBridge.Infrastructure.Downloaders.DoubleDoubleDownloader", "DoubleDouble")]
    [TestCase("MelodyBridge.Application.Services.DownloadManager", "Waterfall")]
    [TestCase("MelodyBridge.Infrastructure.Services.PlaylistStore", "Other")]
    [TestCase("", "Other")]
    public void FromCategory_MapsTheRealLoggers(string category, string expected)
        => Assert.That(LogSources.FromCategory(category), Is.EqualTo(expected));

    [Test]
    public void All_AreUniqueAndContainTheFallback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LogSources.All, Is.Unique);
            Assert.That(LogSources.All, Does.Contain(LogSources.Other));
        });
    }
}
