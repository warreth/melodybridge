using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// slskd plugin against stubbed HTTP responses following the real slskd
/// v0.x API shapes (live-probed against slskd 0.26.1): search creation and
/// polling, responses with locked files and bitrate metadata, transfer
/// enqueue and polling, and the finished file under the downloads folder.
/// No live network access needed.
/// </summary>
[TestFixture]
public class SlskdDownloaderTests
{
    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-slskd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<string> RequestedUrls { get; } = new();
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add($"{request.Method} {request.RequestUri!.AbsoluteUri}");
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(ct));
            return Respond(request);
        }
    }

    /// <summary>A configured plugin with fast poll intervals for tests.</summary>
    private static SlskdDownloader Create(
        StubHttpMessageHandler handler, string? downloadsPath = null, StubPluginConfig? config = null)
    {
        config ??= new StubPluginConfig();
        config.Set("slskd", "api_key", "test-key-0123456789");
        config.Set("slskd", "downloads_path", downloadsPath ?? "/tmp/does-not-matter");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030") };
        return new SlskdDownloader(http, config, NullLogger<SlskdDownloader>.Instance)
        {
            SearchPollInterval = TimeSpan.FromMilliseconds(10),
            SearchPollTimeout = TimeSpan.FromSeconds(2),
            TransferPollInterval = TimeSpan.FromMilliseconds(10),
            TransferTimeout = TimeSpan.FromSeconds(2),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Ok(object body) => Json(HttpStatusCode.OK, body);

    private static object SlskFile(string filename, long size, int? bitRate = null,
        int? length = 200, bool locked = false) => new
    {
        filename,
        size,
        bitRate,
        bitDepth = 16,
        sampleRate = 44100,
        length,
        isLocked = locked,
    };

    private static object Response(string username, bool freeSlot, params object[] files) => new
    {
        username,
        token = 1,
        hasFreeUploadSlot = freeSlot,
        uploadSpeed = 1_000_000,
        queueLength = 0,
        fileCount = files.Length,
        files,
    };

    private static byte[] FlacBytes(int size = 4096)
    {
        var bytes = new byte[size];
        "fLaC"u8.CopyTo(bytes);
        for (var i = 4; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    // ── Availability ─────────────────────────────────────────────────────

    [Test]
    public async Task IsAvailable_Unconfigured_IsFalseWithoutRequests()
    {
        var handler = new StubHttpMessageHandler();
        var config = new StubPluginConfig(); // no api key, no downloads folder
        var http = new HttpClient(handler);
        var downloader = new SlskdDownloader(http, config, NullLogger<SlskdDownloader>.Instance);

        Assert.That(await downloader.IsAvailableAsync(), Is.False);
        Assert.That(handler.RequestedUrls, Is.Empty,
            "an unconfigured plugin is skipped like a missing binary, without a request");
    }

    [Test]
    public async Task IsAvailable_ReadsTheDownloadsFolderFromSlskdOptions()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v0/application"))
                return Ok(new { server = new { isConnected = true, isLoggedIn = true } });
            if (path.EndsWith("/api/v0/options"))
                return Ok(new
                {
                    directories = new { incomplete = "/tmp/incomplete", downloads = "/tmp/downloads" },
                });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var config = new StubPluginConfig();
        config.Set("slskd", "api_key", "test-key-0123456789");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030") };
        var downloader = new SlskdDownloader(http, config, NullLogger<SlskdDownloader>.Instance);

        Assert.That(await downloader.IsAvailableAsync(), Is.True,
            "the downloads folder comes from slskd's own options");
    }

    [Test]
    public async Task Download_UsesTheFolderSlskdReports()
    {
        var downloads = NewDir();
        var output = NewDir();
        var bytes = FlacBytes();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v0/options"))
                return Ok(new { directories = new { downloads } });
            if (request.Method == HttpMethod.Post && path.Contains("/transfers/downloads/"))
                return Ok(new { enqueued = new object[] { new { id = "t1" } } });
            if (path.Contains("/transfers/downloads/"))
            {
                if (!Directory.EnumerateFiles(downloads, "*", SearchOption.AllDirectories).Any())
                    File.WriteAllBytes(Path.Combine(downloads, "Artist - Song.flac"), bytes);
                return Ok(new
                {
                    username = "peer",
                    directories = new object[]
                    {
                        new
                        {
                            directory = "",
                            fileCount = 1,
                            files = new object[]
                            {
                                new { filename = "Music\\Artist - Song.flac", state = "Completed, Succeeded" },
                            },
                        },
                    },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var config = new StubPluginConfig();
        config.Set("slskd", "api_key", "test-key-0123456789");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030") };
        var downloader = new SlskdDownloader(http, config, NullLogger<SlskdDownloader>.Instance)
        {
            TransferPollInterval = TimeSpan.FromMilliseconds(10),
            TransferTimeout = TimeSpan.FromSeconds(2),
        };

        var result = await downloader.DownloadAsync(
            SlskdDownloader.BuildSourceUrl("peer", "Music\\Artist - Song.flac", bytes.Length),
            output, "mb-detected");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(File.Exists(result.FilePath), Is.True,
                "the file is picked up from the folder slskd reported");
        });
    }

    [Test]
    public async Task IsAvailable_LoggedIn_TrueAndCached()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Ok(new
        {
            version = new { full = "0.26.1.0" },
            server = new { state = "Connected, LoggedIn", isConnected = true, isLoggedIn = true },
        });
        var downloader = Create(handler);

        Assert.That(await downloader.IsAvailableAsync(), Is.True);
        Assert.That(await downloader.IsAvailableAsync(), Is.True);
        Assert.That(handler.RequestedUrls, Has.Count.EqualTo(1), "the verdict is cached");
        Assert.That(handler.RequestedUrls[0], Is.EqualTo("GET http://127.0.0.1:5030/api/v0/application"));
    }

    [Test]
    public async Task IsAvailable_NotLoggedIn_False()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = _ => Ok(new { server = new { isConnected = true, isLoggedIn = false } });
        var downloader = Create(handler);

        Assert.That(await downloader.IsAvailableAsync(), Is.False);
    }

    // ── Search ───────────────────────────────────────────────────────────

    [Test]
    public async Task Search_CreatesPollsAndRanksTheResponses()
    {
        var handler = new StubHttpMessageHandler();
        var statusCalls = 0;
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && url.EndsWith("/api/v0/searches"))
                return Ok(new { id = "search-1", state = "InProgress" });
            if (url.EndsWith("/api/v0/searches/search-1"))
                return ++statusCalls == 1
                    ? Ok(new { id = "search-1", state = "InProgress" })
                    : Ok(new { id = "search-1", state = "Completed, ResponseLimitReached" });
            if (url.EndsWith("/api/v0/searches/search-1/responses"))
                return Ok(new object[]
                {
                    Response("peer-remix", freeSlot: true,
                        SlskFile("Music\\Various\\Around the World (Remix).mp3", 5_000_000, bitRate: 320)),
                    Response("peer-flac", freeSlot: true,
                        SlskFile("Music\\Daft Punk\\Homework\\07 Daft Punk - Around the World.flac", 48_000_000)),
                });
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Create(handler);

        var hit = await downloader.SearchAsync(new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.Artist, Is.EqualTo("Daft Punk"));
            Assert.That(hit.Title, Is.EqualTo("Around the World"));
            Assert.That(hit.Duration, Is.EqualTo(TimeSpan.FromSeconds(200)),
                "length seconds map onto the duration");
            Assert.That(hit.SourceUrl, Does.StartWith("slskd:peer-flac:"),
                "the lossless file with the free slot wins the tie");
            Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        });

        Assert.That(handler.RequestedUrls.Any(u => u.StartsWith("DELETE") && u.Contains("/searches/search-1")),
            Is.True, "the search is cleaned up after use");
    }

    [Test]
    public async Task Search_SkipsLockedFiles_AndOtherContainers()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (url.Contains("/responses"))
                return Ok(new object[]
                {
                    Response("peer", freeSlot: false,
                        SlskFile("Music\\Artist - Song.flac", 40_000_000, locked: true),
                        SlskFile("Music\\Artist - Song.wav", 60_000_000),
                        SlskFile("Music\\Artist - Song.mp3", 8_000_000, bitRate: 192)),
                });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "Completed" });
        };
        var downloader = Create(handler);

        var hit = await downloader.SearchAsync(new TrackQuery("Artist", "Song", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Does.StartWith("slskd:peer:"), "only the mp3 was usable");
    }

    [Test]
    public async Task Search_RespectsTheQualityBand()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (url.Contains("/responses"))
                return Ok(new object[]
                {
                    Response("peer", freeSlot: true,
                        SlskFile("Music\\Artist - Song.mp3", 4_000_000, bitRate: 128),
                        SlskFile("Music\\Artist - Song.flac", 40_000_000)),
                });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "Completed" });
        };
        var downloader = Create(handler);

        // A 320 request must not settle for the 128 kbps file.
        var hit = await downloader.SearchAsync(new TrackQuery(
            "Artist", "Song", new DownloadQuality(AudioFormat.Mp3, MinKbps: 320, MaxKbps: 320)));

        Assert.That(hit, Is.Null, "neither the 128 kbps file nor a FLAC fits an exact 320 band");
    }

    [Test]
    public async Task Search_NoUsableResponses_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (url.Contains("/responses")) return Ok(Array.Empty<object>());
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "Completed" });
        };
        var downloader = Create(handler);

        Assert.That(await downloader.SearchAsync(new TrackQuery("Artist", "Song", DownloadQuality.Any)),
            Is.Null);
    }

    [Test]
    public async Task Search_ThatNeverCompletes_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { id = "s" });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Ok(new { id = "s", state = "InProgress" });
        };
        var downloader = Create(handler);

        Assert.That(await downloader.SearchAsync(new TrackQuery("Artist", "Song", DownloadQuality.Any)),
            Is.Null, "a search that never completes is a miss");
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task Download_EnqueuesPollsAndMovesTheFinishedFile()
    {
        var downloads = NewDir();
        var output = NewDir();
        var bytes = FlacBytes();
        var transferPolls = 0;

        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && url.Contains("/transfers/downloads/"))
                return Ok(new { enqueued = new object[] { new { id = "t1", username = "peer" } } });
            if (url.Contains("/transfers/downloads/"))
            {
                transferPolls++;
                var state = transferPolls switch
                {
                    1 => "Queued",
                    2 => "InProgress",
                    _ => "Completed, Succeeded",
                };
                if (state.StartsWith("Completed"))
                    File.WriteAllBytes(
                        Path.Combine(downloads, "Homework", "Daft Punk - Around the World.flac"), bytes);
                return Ok(new
                {
                    username = "peer",
                    directories = new object[]
                    {
                        new
                        {
                            directory = "Homework",
                            fileCount = 1,
                            files = new object[]
                            {
                                new { id = "t1", filename = "MUSIQUES\\Daft Punk\\Homework\\Daft Punk - Around the World.flac", size = bytes.Length, state },
                            },
                        },
                    },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        Directory.CreateDirectory(Path.Combine(downloads, "Homework"));
        var downloader = Create(handler, downloads);

        var sourceUrl = SlskdDownloader.BuildSourceUrl(
            "peer", "MUSIQUES\\Daft Punk\\Homework\\Daft Punk - Around the World.flac", bytes.Length);
        var result = await downloader.DownloadAsync(sourceUrl, output, "mb-slskd-1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".flac"));
            Assert.That(File.ReadAllBytes(result.FilePath!), Is.EqualTo(bytes),
                "the moved file is the peer's file, byte for byte");
            Assert.That(File.Exists(Path.Combine(downloads, "Homework", "Daft Punk - Around the World.flac")),
                Is.False, "the file moved out of slskd's downloads folder");
            Assert.That(handler.RequestBodies.Any(b => b.Contains("\"filename\"") && b.Contains("\"size\"")),
                Is.True, "the enqueue body carries the file reference");
        });
    }

    [Test]
    public async Task Download_TerminalFailure_ReportsTheState()
    {
        var downloads = NewDir();
        var output = NewDir();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post) return Ok(new { enqueued = Array.Empty<object>() });
            return Ok(new
            {
                username = "peer",
                directories = new object[]
                {
                    new
                    {
                        directory = "Music",
                        fileCount = 1,
                        files = new object[] { new { filename = "Music\\Artist - Song.mp3", state = "Completed, Errored" } },
                    },
                },
            });
        };
        var downloader = Create(handler, downloads);

        var result = await downloader.DownloadAsync(
            SlskdDownloader.BuildSourceUrl("peer", "Music\\Artist - Song.mp3", 123), output, "mb-fail");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Completed, Errored"));
        });
    }

    [Test]
    public async Task Download_ThatNeverFinishes_ReportsNotFinished()
    {
        var downloads = NewDir();
        var output = NewDir();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Post) return Ok(new { enqueued = Array.Empty<object>() });
            return Ok(new
            {
                username = "peer",
                directories = new object[]
                {
                    new
                    {
                        directory = "Music",
                        fileCount = 1,
                        files = new object[] { new { filename = "Music\\Artist - Song.mp3", state = "Queued" } },
                    },
                },
            });
        };
        var downloader = Create(handler, downloads);

        var result = await downloader.DownloadAsync(
            SlskdDownloader.BuildSourceUrl("peer", "Music\\Artist - Song.mp3", 123), output, "mb-queued");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("did not finish"));
        });
    }

    [Test]
    public async Task Download_SuccessButNoFile_Fails()
    {
        var downloads = NewDir();
        var output = NewDir();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Post) return Ok(new { enqueued = Array.Empty<object>() });
            return Ok(new
            {
                username = "peer",
                directories = new object[]
                {
                    new
                    {
                        directory = "Music",
                        fileCount = 1,
                        files = new object[] { new { filename = "Music\\Artist - Song.mp3", state = "Completed, Succeeded" } },
                    },
                },
            });
        };
        var downloader = Create(handler, downloads);

        var result = await downloader.DownloadAsync(
            SlskdDownloader.BuildSourceUrl("peer", "Music\\Artist - Song.mp3", 123), output, "mb-nofile");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not in the downloads folder"));
        });
    }

    [Test]
    public async Task Download_NonAudioFile_DeletesAndFails()
    {
        var downloads = NewDir();
        var output = NewDir();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Post) return Ok(new { enqueued = Array.Empty<object>() });
            if (!Directory.EnumerateFiles(downloads, "*", SearchOption.AllDirectories).Any())
                File.WriteAllText(Path.Combine(downloads, "Artist - Song.flac"), "not audio at all");
            return Ok(new
            {
                username = "peer",
                directories = new object[]
                {
                    new
                    {
                        directory = "",
                        fileCount = 1,
                        files = new object[] { new { filename = "Artist - Song.flac", state = "Completed, Succeeded" } },
                    },
                },
            });
        };
        var downloader = Create(handler, downloads);

        var result = await downloader.DownloadAsync(
            SlskdDownloader.BuildSourceUrl("peer", "Artist - Song.flac", 15), output, "mb-junk");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not audio"));
            Assert.That(Directory.GetFiles(output), Is.Empty, "the junk never reaches the playlist folder");
        });
    }

    [Test]
    public async Task Download_InvalidReference_Fails()
    {
        var handler = new StubHttpMessageHandler();
        var downloader = Create(handler);

        var result = await downloader.DownloadAsync("https://example.com/song.mp3", NewDir(), "mb-bad");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not a Soulseek transfer reference"));
            Assert.That(handler.RequestedUrls, Is.Empty);
        });
    }

    // ── Parsing helpers ──────────────────────────────────────────────────

    [TestCase("peer", "MUSIQUES\\Daft Punk\\07 Around the World.flac", 48000000L)]
    [TestCase("user name", "a:b\\c.flac", 1L)]
    public void SourceUrl_RoundTrips(string username, string filename, long size)
    {
        var url = SlskdDownloader.BuildSourceUrl(username, filename, size);
        Assert.That(SlskdDownloader.TryParseSourceUrl(url, out var u, out var f, out var s), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(u, Is.EqualTo(username));
            Assert.That(f, Is.EqualTo(filename));
            Assert.That(s, Is.EqualTo(size));
        });
    }

    [TestCase("https://example.com/x.mp3", false)]
    [TestCase("slskd:only:two", false)]
    [TestCase("", false)]
    public void SourceUrl_RejectsForeignReferences(string input, bool expected)
        => Assert.That(SlskdDownloader.TryParseSourceUrl(input, out _, out _, out _), Is.EqualTo(expected));

    [TestCase("Music\\Daft Punk\\Homework\\07 Daft Punk - Around the World.flac", "Daft Punk", "Around the World")]
    [TestCase("Artist - Song.mp3", "Artist", "Song")]
    [TestCase("01 Song.flac", null, "Song")]
    [TestCase("01. Song.flac", null, "Song")]
    [TestCase("Song.mp3", null, "Song")]
    public void SplitArtistTitle_HandlesPeerNaming(string filename, string? artist, string title)
    {
        var (a, t) = SlskdDownloader.SplitArtistTitle(filename);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(artist));
            Assert.That(t, Is.EqualTo(title));
        });
    }

    [TestCase("a.flac", AudioFormat.Flac)]
    [TestCase("a.FLAC", AudioFormat.Flac)]
    [TestCase("a.mp3", AudioFormat.Mp3)]
    [TestCase("a.m4a", AudioFormat.Aac)]
    [TestCase("a.ogg", null)]
    [TestCase("a.txt", null)]
    public void ContainerFor_MapsDeclaredContainers(string filename, AudioFormat? expected)
        => Assert.That(SlskdDownloader.ContainerFor(filename), Is.EqualTo(expected));

    [TestCase("Completed, Succeeded", true)]
    [TestCase("Completed, Errored", true)]
    [TestCase("Queued", false)]
    [TestCase("InProgress", false)]
    [TestCase("Completed", true)]
    public void TransferIsTerminal_KnowsTheStates(string state, bool expected)
        => Assert.That(SlskdDownloader.TransferIsTerminal(state), Is.EqualTo(expected));

    [TestCase("Completed", true)]
    [TestCase("Completed, ResponseLimitReached", true)]
    [TestCase("InProgress", false)]
    public void SearchIsComplete_KnowsTheStates(string state, bool expected)
        => Assert.That(SlskdDownloader.SearchIsComplete(state), Is.EqualTo(expected));
}
