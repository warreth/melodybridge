using MelodyBridge.Core;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Services;

[TestFixture]
[Category("Services")]
public class DevPanelServiceTests
{
    private DevPanelService _service = null!;

    [SetUp]
    public void Setup()
    {
        _service = new DevPanelService();
    }

    // ── Logging ────────────────────────────────────────────

    [Test]
    public void LogInfo_AddsEntry()
    {
        _service.LogInfo("TestCat", "hello world");
        var logs = _service.GetLogs();
        Assert.Multiple(() =>
        {
            Assert.That(logs, Has.Count.EqualTo(1));
            Assert.That(logs[0].Level, Is.EqualTo("Info"));
            Assert.That(logs[0].Category, Is.EqualTo("TestCat"));
            Assert.That(logs[0].Message, Is.EqualTo("hello world"));
        });
    }

    [Test]
    public void LogWarn_AddsEntry()
    {
        _service.LogWarn("TestCat", "warning message");
        var logs = _service.GetLogs();
        Assert.That(logs[0].Level, Is.EqualTo("Warn"));
    }

    [Test]
    public void LogError_AddsEntry()
    {
        _service.LogError("TestCat", "error occurred", "stack trace");
        var logs = _service.GetLogs();
        Assert.Multiple(() =>
        {
            Assert.That(logs[0].Level, Is.EqualTo("Error"));
            Assert.That(logs[0].Detail, Is.EqualTo("stack trace"));
        });
    }

    [Test]
    public void LogDebug_AddsEntry()
    {
        _service.LogDebug("TestCat", "debug info");
        var logs = _service.GetLogs();
        Assert.That(logs[0].Level, Is.EqualTo("Debug"));
    }

    [Test]
    public void GetLogs_ReturnsNewestFirst()
    {
        _service.LogInfo("A", "first");
        _service.LogInfo("B", "second");
        _service.LogInfo("C", "third");
        var logs = _service.GetLogs();
        Assert.Multiple(() =>
        {
            Assert.That(logs, Has.Count.EqualTo(3));
            Assert.That(logs[0].Message, Is.EqualTo("third"));
            Assert.That(logs[1].Message, Is.EqualTo("second"));
            Assert.That(logs[2].Message, Is.EqualTo("first"));
        });
    }

    [Test]
    public void GetLogs_ReturnsSnapshot_NotLiveReference()
    {
        _service.LogInfo("A", "first");
        var snapshot = _service.GetLogs();
        _service.LogInfo("B", "second");
        Assert.That(snapshot, Has.Count.EqualTo(1), "Snapshot should not reflect new entries");
    }

    [Test]
    public void ClearLogs_RemovesAllEntries()
    {
        _service.LogInfo("A", "msg");
        _service.LogInfo("B", "msg");
        _service.ClearLogs();
        Assert.That(_service.GetLogs(), Is.Empty);
    }

    [Test]
    public void LogsAreCappedAt1000()
    {
        for (int i = 0; i < 1100; i++)
            _service.LogInfo("Cat", $"msg {i}");
        Assert.That(_service.GetLogs(), Has.Count.EqualTo(1000));
    }

    [Test]
    public void Log_WithDetail_StoresDetail()
    {
        _service.Log("Error", "TestCat", "msg", "detail text");
        var log = _service.GetLogs()[0];
        Assert.That(log.Detail, Is.EqualTo("detail text"));
    }

    [Test]
    public void Log_WithoutDetail_DetailIsNull()
    {
        _service.LogInfo("Cat", "msg");
        Assert.That(_service.GetLogs()[0].Detail, Is.Null);
    }

    // ── Search state ───────────────────────────────────────

    [Test]
    public void SearchQuery_DefaultIsNull()
    {
        Assert.That(_service.SearchQuery, Is.Null);
    }

    [Test]
    public void SearchQuery_CanSetAndGet()
    {
        _service.SearchQuery = "Beethoven";
        Assert.That(_service.SearchQuery, Is.EqualTo("Beethoven"));
    }

    [Test]
    public void SearchProviderId_DefaultIsNull()
    {
        Assert.That(_service.SearchProviderId, Is.Null);
    }

    [Test]
    public void SearchResults_DefaultIsEmpty()
    {
        Assert.That(_service.SearchResults, Is.Empty);
    }

    [Test]
    public void SearchResults_CanAddResults()
    {
        _service.SearchResults.Add(new InteractiveSearchResult(
            "Test", "Artist", null, "http://example.com",
            Platform.Tidal, Array.Empty<TrackQuality>(),
            "provider1", "Provider 1"));
        Assert.That(_service.SearchResults, Has.Count.EqualTo(1));
    }

    [Test]
    public void LastSearchResult_DefaultIsNull()
    {
        Assert.That(_service.LastSearchResult, Is.Null);
    }

    // ── Download state ─────────────────────────────────────

    [Test]
    public void DownloadUrl_CanSetAndGet()
    {
        _service.DownloadUrl = "http://example.com/track";
        Assert.That(_service.DownloadUrl, Is.EqualTo("http://example.com/track"));
    }

    [Test]
    public void SelectedProviderId_DefaultIsNull()
    {
        Assert.That(_service.SelectedProviderId, Is.Null);
    }

    [Test]
    public void SelectedQuality_DefaultIsNull()
    {
        Assert.That(_service.SelectedQuality, Is.Null);
    }

    [Test]
    public void LastDownloadResult_DefaultIsNull()
    {
        Assert.That(_service.LastDownloadResult, Is.Null);
    }

    [Test]
    public void Enabled_DefaultIsFalse()
    {
        Assert.That(_service.Enabled, Is.False);
    }

    [Test]
    public void Enabled_CanBeSetToTrue()
    {
        _service.Enabled = true;
        Assert.That(_service.Enabled, Is.True);
    }

    // ── Download queue ─────────────────────────────────────

    [Test]
    public void DownloadQueue_DefaultIsEmpty()
    {
        Assert.That(_service.DownloadQueue, Is.Empty);
    }

    [Test]
    public void DownloadQueue_CanAddTask()
    {
        var task = new DevDownloadTask("task1", "Artist — Song", "http://example.com",
            "TestProvider", "Pending", null, null);
        _service.DownloadQueue.Add(task);
        Assert.That(_service.DownloadQueue, Has.Count.EqualTo(1));
    }

    [Test]
    public void NextDownloadTaskId_ReturnsIncrementingIds()
    {
        var id1 = _service.NextDownloadTaskId();
        var id2 = _service.NextDownloadTaskId();
        Assert.Multiple(() =>
        {
            Assert.That(id1, Is.EqualTo("dev-1"));
            Assert.That(id2, Is.EqualTo("dev-2"));
        });
    }

    [Test]
    public void NextDownloadTaskId_IsThreadSafe()
    {
        var ids = new string[10];
        Parallel.For(0, 10, i =>
        {
            ids[i] = _service.NextDownloadTaskId();
        });
        Assert.That(ids, Is.Unique);
    }

    // ── DevDownloadTask ────────────────────────────────────

    [Test]
    public void DevDownloadTask_DefaultConstructor_SetsDefaults()
    {
        var task = new DevDownloadTask();
        Assert.Multiple(() =>
        {
            Assert.That(task.Id, Is.EqualTo(""));
            Assert.That(task.TrackInfo, Is.EqualTo(""));
            Assert.That(task.Url, Is.EqualTo(""));
            Assert.That(task.ProviderName, Is.EqualTo(""));
            Assert.That(task.Status, Is.EqualTo("Pending"));
            Assert.That(task.ResultPath, Is.Null);
            Assert.That(task.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void DevDownloadTask_ParameterizedConstructor_SetsProperties()
    {
        var task = new DevDownloadTask("id1", "Track", "http://url",
            "Prov", "Completed", "/path/to/file.mp3", null);
        Assert.Multiple(() =>
        {
            Assert.That(task.Id, Is.EqualTo("id1"));
            Assert.That(task.TrackInfo, Is.EqualTo("Track"));
            Assert.That(task.Url, Is.EqualTo("http://url"));
            Assert.That(task.ProviderName, Is.EqualTo("Prov"));
            Assert.That(task.Status, Is.EqualTo("Completed"));
            Assert.That(task.ResultPath, Is.EqualTo("/path/to/file.mp3"));
            Assert.That(task.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void DevDownloadTask_PropertiesAreSettable()
    {
        var task = new DevDownloadTask();
        task.Id = "new-id";
        task.Status = "Failed";
        task.ErrorMessage = "Something went wrong";
        Assert.Multiple(() =>
        {
            Assert.That(task.Id, Is.EqualTo("new-id"));
            Assert.That(task.Status, Is.EqualTo("Failed"));
            Assert.That(task.ErrorMessage, Is.EqualTo("Something went wrong"));
        });
    }
}
