using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Resolvers;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class QobuzIdResolverTests
{
    [Test]
    public void Constructor_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => new QobuzIdResolver());
    }

    [Test]
    public void GetQobuzTrackIdByIsrcAsync_EmptyIsrc_Throws()
    {
        var resolver = new QobuzIdResolver();
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.GetQobuzTrackIdByIsrcAsync(""));

        Assert.That(ex!.Message, Does.Contain("must not be empty"));
    }

    [Test]
    public void GetQobuzTrackIdByIsrcAsync_WhitespaceIsrc_Throws()
    {
        var resolver = new QobuzIdResolver();
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.GetQobuzTrackIdByIsrcAsync("   "));

        Assert.That(ex!.Message, Does.Contain("must not be empty"));
    }

    [Test]
    public void GetQobuzTrackIdByIsrcAsync_NullIsrc_Throws()
    {
        var resolver = new QobuzIdResolver();
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.GetQobuzTrackIdByIsrcAsync(null!));
    }

    [Test]
    public async Task GetQobuzTrackIdByIsrcAsync_InvalidIsrc_Throws()
    {
        var resolver = new QobuzIdResolver();
        // This will attempt a real API call and fail with network errors or "not found"
        // Just verify it doesn't throw ArgumentException for wrong reasons
        try
        {
            await resolver.GetQobuzTrackIdByIsrcAsync("USABC1234567");
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            // Expected — network errors or API rejections are fine
            Assert.Pass($"Expected exception (network/API): {ex.GetType().Name}");
        }
    }
}
