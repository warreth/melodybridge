using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The manual download service: one search box becomes a real plugin
/// query, a picked candidate is tagged and renamed on disk, and a pasted
/// link is named from its resolved metadata or the link itself. The tests
/// use real files and a real manager so the filename, tags and MELODY_ID
/// are read back from the result.
/// </summary>
[TestFixture]
public class ManualDownloadServiceTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-manual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A plugin that records the query and writes a real MP3.</summary>
    private sealed class StubPlugin : IDownloader
    {
        public string Id => "stub";
        public string Name => "Stub Source";
        public PluginCapabilities Capabilities { get; init; } = PluginCapabilities.Any;
        public bool Available { get; init; } = true;
        public bool Succeeds { get; init; } = true;
        public string? Error { get; init; }
        public DownloaderSearchHit? Hit { get; init; }
        public TrackQuery? LastQuery { get; private set; }
        public string? LastSourceUrl { get; private set; }
        public DownloadQuality? LastQuality { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(Available);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Hit);
        }

        public Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<DownloaderSearchHit>>(
                Hit is null ? Array.Empty<DownloaderSearchHit>() : new[] { Hit });
        }

        public Task<CandidateAudioQuality?> ProbeQualityAsync(
            string sourceUrl, CancellationToken ct = default)
            => Task.FromResult<CandidateAudioQuality?>(null);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            LastSourceUrl = sourceUrl;
            LastQuality = quality;
            if (!Succeeds)
                return Task.FromResult(new DownloaderDownloadResult(
                    false, null, Error ?? "the source failed"));

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId ?? "x"}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private sealed class StubMetadataResolver(UrlTrackMetadata? result) : IUrlMetadataResolver
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<UrlTrackMetadata?> ResolveAsync(string url, CancellationToken ct = default)
        {
            Calls++;
            LastUrl = url;
            return Task.FromResult(result);
        }
    }

    private static ManualDownloadService Service(
        IDownloader plugin, IUrlMetadataResolver? metadata = null)
    {
        var registry = new ListDownloaderRegistry(plugin);
        var manager = new DownloadManager(registry, NullLogger<DownloadManager>.Instance);
        return new ManualDownloadService(
            manager, registry,
            metadata ?? new StubMetadataResolver(null),
            NullLogger<ManualDownloadService>.Instance);
    }

    [TestCase("The Artist - The Song", "The Artist", "The Song")]
    [TestCase("  The Artist - The Song  ", "The Artist", "The Song")]
    [TestCase("A - B - C", "A", "B - C")]
    [TestCase("Just a title", "", "Just a title")]
    [TestCase("", "", "")]
    public void ParseQuery_SplitsArtistAndTitle(string raw, string artist, string title)
    {
        var parsed = ManualDownloadService.ParseQuery(raw);
        Assert.That(parsed.Artist, Is.EqualTo(artist));
        Assert.That(parsed.Title, Is.EqualTo(title));
    }

    [TestCase("https://www.youtube.com/watch?v=abc", true)]
    [TestCase("http://example.com/song", true)]
    [TestCase("ftp://example.com/song", false)]
    [TestCase("example.com/song", false)]
    [TestCase("", false)]
    public void TryParseLink_OnlyAcceptsHttpLinks(string value, bool expected)
    {
        Assert.That(ManualDownloadService.TryParseLink(value, out _), Is.EqualTo(expected));
    }

    [Test]
    public async Task Search_PassesTheSplitQueryToThePlugins()
    {
        var plugin = new StubPlugin
        {
            Hit = new DownloaderSearchHit(
                "The Song", "The Artist", "https://stub/1", TimeSpan.FromSeconds(2)),
        };
        var service = Service(plugin);

        var outcome = await service.SearchAsync("The Artist - The Song", DownloadQuality.Any);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Candidates, Has.Count.EqualTo(1));
            Assert.That(plugin.LastQuery, Is.Not.Null);
            Assert.That(plugin.LastQuery!.Artist, Is.EqualTo("The Artist"));
            Assert.That(plugin.LastQuery.Title, Is.EqualTo("The Song"));
        });
    }

    [Test]
    public async Task DownloadCandidate_WritesTagsMelodyIdAndACleanFileName()
    {
        var plugin = new StubPlugin
        {
            Hit = new DownloaderSearchHit(
                "The Song", "The Artist", "https://stub/1", TimeSpan.FromSeconds(2)),
        };
        var service = Service(plugin);
        var outcome = await service.SearchAsync("The Artist - The Song", DownloadQuality.Any);
        var candidate = outcome.Candidates.Single();

        var result = await service.DownloadCandidateAsync(
            "The Artist - The Song", candidate, _dir, DownloadQuality.Any);

        var expected = Path.Combine(_dir, "The Artist - The Song.mp3");
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.FilePath, Is.EqualTo(expected));
            Assert.That(File.Exists(expected), Is.True, "the file is on disk");
            Assert.That(result.PluginName, Is.EqualTo("Stub Source"));
        });

        using var tags = TagLib.File.Create(expected);
        var melodyId = TaglibHelper.ReadMelodyId(expected);
        Assert.Multiple(() =>
        {
            Assert.That(tags.Tag.Title, Is.EqualTo("The Song"));
            Assert.That(tags.Tag.Performers, Does.Contain("The Artist"));
            Assert.That(melodyId, Does.StartWith("manual-"),
                "a manual file carries the identity the scanner uses");
            Assert.That(result.Warning, Is.Null, "a matching file needs no warning");
        });
    }

    [Test]
    public async Task DownloadCandidate_WithoutAnArtist_KeepsTheTitleAsTheFileName()
    {
        var plugin = new StubPlugin
        {
            Hit = new DownloaderSearchHit(
                "Only a title", null, "https://stub/1", TimeSpan.FromSeconds(2)),
        };
        var service = Service(plugin);

        var result = await service.DownloadCandidateAsync(
            "Only a title",
            new DownloadCandidate(
                plugin.Id, plugin.Name, "Only a title", null, TimeSpan.FromSeconds(2),
                null, "https://stub/1", MatchConfidence.High),
            _dir, DownloadQuality.Any);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(Path.GetFileName(result.FilePath), Is.EqualTo("Only a title.mp3"));
        });
    }

    [Test]
    public async Task DownloadCandidate_ReportsThePluginsRealFailure()
    {
        var plugin = new StubPlugin
        {
            Succeeds = false,
            Error = "the source is geo-blocked",
        };
        var service = Service(plugin);
        var candidate = new DownloadCandidate(
            plugin.Id, plugin.Name, "The Song", "The Artist", null,
            null, "https://stub/1", MatchConfidence.High);

        var result = await service.DownloadCandidateAsync(
            "The Artist - The Song", candidate, _dir, DownloadQuality.Any);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("geo-blocked"));
        });
    }

    [Test]
    public async Task DownloadUrl_ResolvesMetadataWhenFieldsAreEmpty()
    {
        var plugin = new StubPlugin();
        var metadata = new StubMetadataResolver(new UrlTrackMetadata(
            "Resolved Artist", "Resolved Title", TimeSpan.FromSeconds(2)));
        var service = Service(plugin, metadata);

        var result = await service.DownloadUrlAsync(
            "https://example.com/song", null, null, _dir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(metadata.Calls, Is.EqualTo(1));
            Assert.That(metadata.LastUrl, Is.EqualTo("https://example.com/song"));
            Assert.That(Path.GetFileName(result.FilePath),
                Is.EqualTo("Resolved Artist - Resolved Title.mp3"));
            Assert.That(plugin.LastSourceUrl, Is.EqualTo("https://example.com/song"));
        });

        using var tags = TagLib.File.Create(result.FilePath!);
        Assert.Multiple(() =>
        {
            Assert.That(tags.Tag.Title, Is.EqualTo("Resolved Title"));
            Assert.That(tags.Tag.Performers, Does.Contain("Resolved Artist"));
            Assert.That(TaglibHelper.ReadMelodyId(result.FilePath!), Does.StartWith("manual-"),
                "a direct link download is scannable too");
        });
    }

    [Test]
    public async Task DownloadUrl_KeepsTheUsersOwnFieldsAndSkipsResolution()
    {
        var plugin = new StubPlugin();
        var metadata = new StubMetadataResolver(null);
        var service = Service(plugin, metadata);

        var result = await service.DownloadUrlAsync(
            "https://example.com/song", "Given Artist", "Given Title", _dir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(metadata.Calls, Is.Zero,
                "both fields are already known; the link does not need a lookup");
            Assert.That(Path.GetFileName(result.FilePath),
                Is.EqualTo("Given Artist - Given Title.mp3"));
        });
    }

    [Test]
    public async Task DownloadUrl_WithoutMetadata_UsesTheLinkSlug()
    {
        var plugin = new StubPlugin();
        var service = Service(plugin); // the resolver returns null

        var result = await service.DownloadUrlAsync(
            "https://example.com/the-greatest_song", null, null, _dir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(Path.GetFileName(result.FilePath), Is.EqualTo("the greatest song.mp3"));
        });
    }

    [Test]
    public async Task DownloadUrl_RejectsSomethingThatIsNotALink()
    {
        var plugin = new StubPlugin();
        var service = Service(plugin);

        var result = await service.DownloadUrlAsync("not a link", null, null, _dir);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("http"));
            Assert.That(plugin.LastSourceUrl, Is.Null, "nothing was downloaded");
        });
    }

    [Test]
    public async Task DownloadCandidate_FlagsARealLengthMismatchButKeepsTheFile()
    {
        // The candidate says four minutes; the stub writes a one-second
        // MP3. ffprobe sees the difference, and the file survives because
        // the human made the pick. This is the honest warning path.
        var plugin = new StubPlugin
        {
            Hit = new DownloaderSearchHit(
                "Long Song", "The Artist", "https://stub/1", TimeSpan.FromMinutes(4)),
        };
        var service = Service(plugin);
        var outcome = await service.SearchAsync("The Artist - Long Song", DownloadQuality.Any);

        var result = await service.DownloadCandidateAsync(
            "The Artist - Long Song", outcome.Candidates.Single(), _dir, DownloadQuality.Any);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(result.Warning, Is.Not.Null.And.Contain("differs"));
        });
    }
}
