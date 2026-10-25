using MelodyBridge.Application;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The dashboard asks the media servers how they are, and the answer is
/// cached for a while. The cache only pays off when it outlives the
/// request, so the probe and the profile store it reads have to be one
/// for the whole app: a page load must not ask an unreachable server
/// again what the last load already asked.
/// </summary>
[TestFixture]
public class MediaServerProbeLifetimeTests
{
    [Test]
    public void TheProbeAndTheProfileStore_LiveForTheWholeApp()
    {
        var services = new ServiceCollection();
        services.AddMelodyBridge();

        Assert.Multiple(() =>
        {
            Assert.That(LifetimeOf<IMediaServerStatusProbe>(services), Is.EqualTo(ServiceLifetime.Singleton),
                "the cached verdict has to survive the request that made it");
            Assert.That(LifetimeOf<MediaServerProfileStore>(services), Is.EqualTo(ServiceLifetime.Singleton));
        });
    }

    private static ServiceLifetime LifetimeOf<T>(IServiceCollection services)
        => services.Single(d => d.ServiceType == typeof(T)).Lifetime;
}
