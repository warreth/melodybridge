using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Profile CRUD against a real SQLite file: the settings key is written
/// and read back through the actual SettingsStore.
/// </summary>
[TestFixture]
[Category("Integration")]
public class MediaServerProfileStoreTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private MediaServerProfileStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-profiles-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var settings = new SettingsStore(_factory);
        _store = new MediaServerProfileStore(settings);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Save_And_Read_Back_Round_Trip()
    {
        var profile = new MediaServerProfile
        {
            Name = "Living room",
            BaseUrl = "http://192.168.1.10:8096",
            ApiKey = "secret",
            Kind = "Jellyfin",
        };
        await _store.SaveAsync(profile);

        var all = await _store.GetAllAsync();
        Assert.That(all.Count, Is.EqualTo(1));
        Assert.That(all[0].Name, Is.EqualTo("Living room"));
        Assert.That(all[0].ApiKey, Is.EqualTo("secret"));
        Assert.That(all[0].Id, Is.EqualTo(profile.Id));
    }

    [Test]
    public async Task Update_By_Id_Keeps_One_Row()
    {
        var profile = new MediaServerProfile { Name = "Old", BaseUrl = "http://a" };
        await _store.SaveAsync(profile);

        var edited = (await _store.GetAllAsync())[0];
        edited.Name = "New";
        edited.BaseUrl = "http://b";
        await _store.SaveAsync(edited);

        var all = await _store.GetAllAsync();
        Assert.That(all.Count, Is.EqualTo(1), "save by id updates, never duplicates");
        Assert.That(all[0].Name, Is.EqualTo("New"));
        Assert.That(all[0].BaseUrl, Is.EqualTo("http://b"));
    }

    [Test]
    public async Task Delete_Removes_Only_That_Profile()
    {
        var a = new MediaServerProfile { Name = "A", BaseUrl = "http://a" };
        var b = new MediaServerProfile { Name = "B", BaseUrl = "http://b" };
        await _store.SaveAsync(a);
        await _store.SaveAsync(b);

        Assert.That(await _store.DeleteAsync(a.Id), Is.True);
        Assert.That(await _store.DeleteAsync(a.Id), Is.False, "second delete has nothing left");

        var all = await _store.GetAllAsync();
        Assert.That(all.Count, Is.EqualTo(1));
        Assert.That(all[0].Name, Is.EqualTo("B"));
    }

    [Test]
    public async Task Corrupted_Json_Yields_Empty_List_Not_Crash()
    {
        var settings = new SettingsStore(_factory);
        await settings.SetAsync("media_server_profiles", "{not json");
        var store = new MediaServerProfileStore(settings);

        var all = await store.GetAllAsync();
        Assert.That(all, Is.Empty, "a broken blob degrades to empty, never throws");
    }
}
