using MelodyBridge.Application;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The composition of the download path. The store, the linker and the
/// coordinator must share one claim registry: a batch that starts while
/// a single-track download runs has to see that download as live, and
/// only a shared registry can tell it apart from a claim a crashed run
/// left behind.
/// </summary>
[TestFixture]
public class DownloadClaimWiringTests
{
    private static object? Field(object instance, string name)
        => instance.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(instance);

    [Test]
    public void TheStoreAndTheCoordinatorShareTheRegisteredRegistry()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"mb-wire-{Guid.NewGuid():N}.db")}"));
        services.AddMelodyBridge();
        var provider = services.BuildServiceProvider();

        var registered = provider.GetRequiredService<ITrackClaimRegistry>();
        var coordinator = provider.GetRequiredService<DownloadCoordinator>();

        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PlaylistStore>();

        Assert.Multiple(() =>
        {
            Assert.That(Field(store, "_claims"), Is.SameAs(registered),
                "the store hands the app-wide registry to the pipeline and the linker");
            Assert.That(Field(coordinator, "_claims"), Is.SameAs(registered),
                "the coordinator reads the same registry");
            Assert.That(scope.ServiceProvider.GetRequiredService<ITrackClaimRegistry>(),
                Is.SameAs(registered),
                "every scope sees the same registry");
        });
    }

    [Test]
    public void TheRegistryIsASingleton()
    {
        var services = new ServiceCollection();
        services.AddMelodyBridge();
        var provider = services.BuildServiceProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Multiple(() =>
        {
            Assert.That(first.ServiceProvider.GetRequiredService<ITrackClaimRegistry>(),
                Is.SameAs(second.ServiceProvider.GetRequiredService<ITrackClaimRegistry>()));
            Assert.That(provider.GetRequiredService<LiveTrackClaims>(),
                Is.SameAs(provider.GetRequiredService<ITrackClaimRegistry>()),
                "the app-wide implementation is the one that is handed out");
        });
    }
}
