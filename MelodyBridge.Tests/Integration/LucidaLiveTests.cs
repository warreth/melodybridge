namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Lucida against the real lucida.to. Lucida sits behind a Cloudflare
/// challenge, so without a reachable FlareSolverr the plugin must honestly
/// report itself unavailable instead of producing garbage.
/// A live end-to-end run needs FLARESOLVERR_URL set to a working instance.
/// </summary>
[TestFixture]
[Category("Live")]
public class LucidaLiveTests
{
    [Test]
    public async Task WithoutSolver_PluginReportsUnavailable()
    {
        var solverUrl = Environment.GetEnvironmentVariable("FLARESOLVERR_URL");
        if (!string.IsNullOrWhiteSpace(solverUrl))
            Assert.Ignore("FLARESOLVERR_URL is set: covered by the live end-to-end test");

        var http = new HttpClient { BaseAddress = new Uri("https://lucida.to") };
        var downloader = new MelodyBridge.Infrastructure.Lucida.LucidaDownloader(
            http,
            new MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver(
                http,
                Microsoft.Extensions.Options.Options.Create(
                    new MelodyBridge.Infrastructure.Cloudflare.FlareSolverrOptions { Url = "off" }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MelodyBridge.Infrastructure.Lucida.LucidaDownloader>.Instance);

        Assert.That(await downloader.IsAvailableAsync(), Is.False,
            "no solver configured: the plugin must stay out of the waterfall");
    }

    [Test]
    public async Task WithSolver_SearchesAndReturnsHit()
    {
        var solverUrl = Environment.GetEnvironmentVariable("FLARESOLVERR_URL");
        if (string.IsNullOrWhiteSpace(solverUrl))
            Assert.Ignore("FLARESOLVERR_URL not set: start FlareSolverr and set it to run this test");

        var http = new HttpClient { BaseAddress = new Uri("https://lucida.to") };
        var downloader = new MelodyBridge.Infrastructure.Lucida.LucidaDownloader(
            http,
            new MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver(
                http,
                Microsoft.Extensions.Options.Options.Create(
                    new MelodyBridge.Infrastructure.Cloudflare.FlareSolverrOptions { Url = solverUrl }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MelodyBridge.Infrastructure.Lucida.LucidaDownloader>.Instance);

        Assert.That(await downloader.IsAvailableAsync(), Is.True);

        var hit = await downloader.SearchAsync(
            "Rick Astley", "Never Gonna Give You Up", MelodyBridge.Core.DownloadQuality.Any);
        Assert.That(hit, Is.Not.Null, "the search must find a well-known track");
        Assert.That(hit!.SourceUrl, Is.Not.Empty);
    }
}
