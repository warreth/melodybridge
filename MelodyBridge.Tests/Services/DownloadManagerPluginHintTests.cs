using System.Collections.Concurrent;
using MelodyBridge.Application.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The download manager is a singleton, and a manual download adds a
/// melody id that never comes back. The plugin hints are a display
/// convenience, so they keep a ceiling instead of growing for the life
/// of the process.
/// </summary>
[TestFixture]
public class DownloadManagerPluginHintTests
{
    [Test]
    public void RememberPlugin_KeepsTheNewestHint_AndTrimsTheMapToItsCap()
    {
        var map = new ConcurrentDictionary<string, string>();

        for (var i = 0; i < 2500; i++)
            DownloadManager.RememberPlugin(map, $"manual-{i}", "ytdlp");

        Assert.Multiple(() =>
        {
            Assert.That(map.Count, Is.LessThanOrEqualTo(2000),
                "the map does not grow with every manual download");
            Assert.That(map["manual-2499"], Is.EqualTo("ytdlp"),
                "the hint that was just written is still there");
        });
    }

    [Test]
    public void RememberPlugin_OverwritesTheHintForTheSameId()
    {
        var map = new ConcurrentDictionary<string, string>();

        DownloadManager.RememberPlugin(map, "mel-1", "ytdlp");
        DownloadManager.RememberPlugin(map, "mel-1", "soulseek");

        Assert.That(map["mel-1"], Is.EqualTo("soulseek"),
            "the newest plugin that succeeded wins");
    }
}
