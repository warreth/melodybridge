using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The plugin registry is a singleton whose enable and priority state the
/// UI pages write while the download coordinator's workers read it. The
/// plain dictionaries behind that sat one write away from a broken
/// enumeration inside a download, and two first readers could both
/// register the same plugin row and lose the real stored state to the
/// "enable everything" fallback.
/// </summary>
[TestFixture]
public class DownloaderRegistryConcurrencyTests
{
    private TestSqliteFactory _factory = null!;

    private sealed class DummyUploader : IDownloader
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description => "";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "unused"));
    }

    [SetUp]
    public async Task Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mb-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _factory = new TestSqliteFactory(Path.Combine(root, "registry.db"));
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    private DownloaderRegistry NewRegistry()
        => new(
            [new DummyUploader { Id = "one", Name = "One" },
             new DummyUploader { Id = "two", Name = "Two" }],
            _factory, new PluginConfigStore(_factory),
            NullLogger<DownloaderRegistry>.Instance);

    [Test]
    public async Task TwoFirstReaders_RegisterEachPluginOnce()
    {
        var registry = NewRegistry();

        var first = Task.Run(() => registry.GetEnabled(), CancellationToken.None);
        var second = Task.Run(() => registry.GetEnabled(), CancellationToken.None);
        await Task.WhenAll(first, second);

        await using var db = _factory.CreateDbContext();
        var rows = await db.ProviderStates.ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2), "one stored row per plugin");
        Assert.That(await db.ProviderStates.CountAsync(r => r.ProviderId == "one"), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentReadsAndWrites_NeverCrashAWorker()
    {
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var registry = NewRegistry();
        Assert.DoesNotThrow(() => registry.GetEnabled(), "the cache warms up first");

        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ui = Task.Run(async () =>
        {
            var flip = false;
            while (!stop.IsCancellationRequested)
            {
                flip = !flip;
                await registry.SetEnabledAsync("one", flip);
                await registry.SetPriorityAsync("two", flip ? 7 : 9);
            }
        }, CancellationToken.None);

        var workers = Enumerable.Range(0, 8).Select(n => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = registry.GetEnabled();
                    _ = registry.IsEnabled("two");
                    _ = registry.GetPriorityAsync("one").Result;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }, CancellationToken.None)).ToArray();

        await Task.WhenAll(workers);
        await ui;

        Assert.That(exceptions, Is.Empty, "the registry must never throw while it is used");
        // The stored state ends up consistent with the last write either way.
        await using var db = _factory.CreateDbContext();
        Assert.That((await db.ProviderStates.ToListAsync()), Has.Count.EqualTo(2));
    }
}
