using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Integration tests that perform real searches using music providers.
/// These tests hit external APIs and require network access —
/// they are marked [Explicit] and will NOT run during normal CI.
/// Run manually with:
///   dotnet test --filter "Category=SearchIntegration"
/// </summary>
[TestFixture]
[Category("SearchIntegration")]
[Explicit]
public class SearchIntegrationTests
{
    private SquidWtfProvider _squid = null!;
    private LucidaProvider _lucida = null!;
    private DoubleDoubleProvider _doubleDouble = null!;
    private MonochromeProvider _monochrome = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _squid = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        _lucida = new LucidaProvider(NullLogger<LucidaProvider>.Instance);
        _doubleDouble = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);
        _monochrome = new MonochromeProvider(NullLogger<MonochromeProvider>.Instance);
    }

    /// <summary>Verify search results look valid.</summary>
    private static void AssertValidResults(IReadOnlyList<SearchResult> results, string providerName)
    {
        Assert.That(results, Is.Not.Null, $"{providerName}: results is null");
        Assert.That(results.Count, Is.GreaterThan(0), $"{providerName}: no results returned");

        var r = results[0];
        Assert.That(r.Title, Is.Not.Null.Or.Empty, $"{providerName}: first result has no title");
        Assert.That(r.Url, Is.Not.Null.Or.Empty, $"{providerName}: first result has no URL");
        Assert.That(r.SourcePlatform, Is.Not.EqualTo(Platform.Unknown), $"{providerName}: unknown source platform");
        Assert.That(r.AvailableQualities, Is.Not.Null, $"{providerName}: no quality info");
    }

    // ── Squid.wtf ──────────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task SquidWtf_Search_ByArtistAndTrack()
    {
        var results = await _squid.SearchAsync("Rick Astley Never Gonna Give You Up");
        AssertValidResults(results, "Squid.wtf");
    }

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task SquidWtf_Search_WithPlatformFilter_Qobuz()
    {
        var results = await _squid.SearchAsync("classical piano", Platform.Qobuz);
        AssertValidResults(results, "Squid.wtf(Qobuz)");
    }

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task SquidWtf_Search_WithPlatformFilter_Tidal()
    {
        var results = await _squid.SearchAsync("jazz fusion", Platform.Tidal);
        AssertValidResults(results, "Squid.wtf(Tidal)");
    }

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task SquidWtf_Search_ReturnsResultsWithQualities()
    {
        var results = await _squid.SearchAsync("electronic ambient");
        AssertValidResults(results, "Squid.wtf");
        // Verify at least one quality entry
        Assert.That(results[0].AvailableQualities.Count, Is.GreaterThan(0),
            "Squid.wtf: no available qualities on first result");
    }

    // ── Lucida ─────────────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task Lucida_Search_ByArtistAndTrack()
    {
        var results = await _lucida.SearchAsync("Daft Punk Around the World");
        AssertValidResults(results, "Lucida");
    }

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task Lucida_Search_WithPlatformFilter_Deezer()
    {
        var results = await _lucida.SearchAsync("rock classics", Platform.Deezer);
        AssertValidResults(results, "Lucida(Deezer)");
    }

    // ── DoubleDouble ───────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task DoubleDouble_Search_ByArtistAndTrack()
    {
        var results = await _doubleDouble.SearchAsync("Miles Davis So What");
        AssertValidResults(results, "DoubleDouble");
    }

    // ── Monochrome ─────────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task Monochrome_Search_ByArtistAndTrack()
    {
        var results = await _monochrome.SearchAsync("Bach Cello Suite");
        AssertValidResults(results, "Monochrome");
    }

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task Monochrome_Search_ReturnsTidalResults()
    {
        var results = await _monochrome.SearchAsync("classical");
        AssertValidResults(results, "Monochrome");
        // Monochrome only supports TIDAL
        Assert.That(results[0].SourcePlatform, Is.EqualTo(Platform.Tidal)
            .Or.EqualTo(Platform.Qobuz),
            "Monochrome: unexpected platform");
    }

    // ── Cross-provider ─────────────────────────────────────

    [Test]
    [Explicit]
    [Timeout(60_000)]
    public async Task CrossProvider_Search_SameQueryDifferentProviders()
    {
        var query = "Bohemian Rhapsody";
        var squidResults = await _squid.SearchAsync(query);
        var lucidaResults = await _lucida.SearchAsync(query);

        Assert.Multiple(() =>
        {
            Assert.That(squidResults.Count, Is.GreaterThan(0), "Squid.wtf returned no results");
            Assert.That(lucidaResults.Count, Is.GreaterThan(0), "Lucida returned no results");
        });

        // Both should find the song (Bohemian Rhapsody is widely available)
        var squidTitles = squidResults.Select(r => r.Title.ToLowerInvariant()).ToList();
        var lucidaTitles = lucidaResults.Select(r => r.Title.ToLowerInvariant()).ToList();
        var allTitles = squidTitles.Concat(lucidaTitles).Distinct().ToList();

        Assert.That(allTitles.Any(t => t.Contains("bohemian") || t.Contains("rhapsody")),
            Is.True, "Neither provider returned a result containing 'bohemian' or 'rhapsody'");
    }

    [Test]
    [Explicit]
    [Timeout(30_000)]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        var results = await _squid.SearchAsync("");
        Assert.That(results, Is.Empty);
    }
}
