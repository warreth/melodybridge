using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The interface mode: a first install starts simple, an existing database
/// stays advanced, and an explicit choice wins on the next start.
/// </summary>
[TestFixture]
public class UiModeServiceTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-uimode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "state.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private UiModeService Service(bool existingInstall)
        => new(new SettingsStore(_factory), new InstallState(existingInstall));

    [Test]
    public async Task FirstInstall_StartsSimpleUntilTheUserChooses()
    {
        var service = Service(existingInstall: false);
        await service.EnsureLoadedAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.IsAdvanced, Is.False);
            Assert.That(service.HasChosen, Is.False);
        });
    }

    [Test]
    public async Task ExistingInstall_DefaultsToAdvanced()
    {
        var service = Service(existingInstall: true);
        await service.EnsureLoadedAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.IsAdvanced, Is.True);
            Assert.That(service.HasChosen, Is.False);
        });
    }

    [Test]
    public async Task Choice_PersistsAndRaisesChanged()
    {
        var service = Service(existingInstall: false);
        var changed = 0;
        service.Changed += () => changed++;
        await service.EnsureLoadedAsync();

        await service.SetAsync(advanced: true);

        Assert.Multiple(() =>
        {
            Assert.That(service.IsAdvanced, Is.True);
            Assert.That(service.HasChosen, Is.True);
            Assert.That(changed, Is.EqualTo(1));
        });

        var nextStart = Service(existingInstall: false);
        await nextStart.EnsureLoadedAsync();
        Assert.That(nextStart.IsAdvanced, Is.True, "the saved choice wins over the install default");
        Assert.That(nextStart.HasChosen, Is.True);
    }
}
