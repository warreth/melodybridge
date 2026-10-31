using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;
using static MelodyBridge.Tests.Infrastructure.SlskdTestSupport;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The slskd downloader against stubbed HTTP responses following the real
/// slskd v0.x API shapes (live-probed against slskd 0.26.1): transfer
/// enqueue and polling, the finished file under the downloads folder, the
/// availability probe and the plugin card metadata. The search lives in
/// SlskdSearchProviderTests. No live network access needed.
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
            SlskdSearchProvider.BuildSourceUrl("peer", "Music\\Artist - Song.flac", bytes.Length),
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
    public async Task Download_AfterABaseUrlChange_UsesTheNewInstancesFolder()
    {
        var folderOne = NewDir();
        var folderTwo = NewDir();
        var bytes = FlacBytes();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var downloads = request.RequestUri.Host == "slskd-two.local" ? folderTwo : folderOne;
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
        config.Set("slskd", "base_url", "http://slskd-one.local:5030");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030") };
        var downloader = new SlskdDownloader(http, config, NullLogger<SlskdDownloader>.Instance)
        {
            TransferPollInterval = TimeSpan.FromMilliseconds(10),
            TransferTimeout = TimeSpan.FromSeconds(2),
        };
        var sourceUrl = SlskdSearchProvider.BuildSourceUrl(
            "peer", "Music\\Artist - Song.flac", bytes.Length);

        var first = await downloader.DownloadAsync(sourceUrl, NewDir(), "mb-one");
        Assert.That(first.Success, Is.True, first.ErrorMessage);
        Assert.That(downloader.DetectedDownloadsPath, Is.EqualTo(folderOne));

        // The user points the plugin at another slskd instance: the folder
        // of the previous one must not stick.
        config.Set("slskd", "base_url", "http://slskd-two.local:5030");

        var second = await downloader.DownloadAsync(sourceUrl, NewDir(), "mb-two");

        Assert.Multiple(() =>
        {
            Assert.That(second.Success, Is.True, second.ErrorMessage);
            Assert.That(downloader.DetectedDownloadsPath, Is.EqualTo(folderTwo),
                "the folder cache belongs to the instance that reported it");
        });
    }

    [Test]
    public async Task Download_WhenTheFileIsNotWhereSlskdSaid_ForgetsTheFolder()
    {
        var staleFolder = NewDir();
        var realFolder = NewDir();
        var reported = staleFolder;
        var bytes = FlacBytes();
        var handler = new StubHttpMessageHandler();
        handler.Respond = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v0/options"))
                return Ok(new { directories = new { downloads = reported } });
            if (request.Method == HttpMethod.Post && path.Contains("/transfers/downloads/"))
                return Ok(new { enqueued = new object[] { new { id = "t1" } } });
            if (path.Contains("/transfers/downloads/"))
            {
                // slskd moved its downloads folder: the file lands where the
                // plugin is not looking.
                if (!Directory.EnumerateFiles(realFolder, "*", SearchOption.AllDirectories).Any())
                    File.WriteAllBytes(Path.Combine(realFolder, "Artist - Song.flac"), bytes);
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
        var sourceUrl = SlskdSearchProvider.BuildSourceUrl(
            "peer", "Music\\Artist - Song.flac", bytes.Length);

        var first = await downloader.DownloadAsync(sourceUrl, NewDir(), "mb-stale");

        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.False, "the file is not in the folder slskd reported");
            Assert.That(first.ErrorMessage, Does.Contain("not in the downloads folder"));
            Assert.That(downloader.DetectedDownloadsPath, Is.Null,
                "a folder that did not hold the file must not stay cached");
        });

        // With the fresh answer the same download works.
        reported = realFolder;
        var second = await downloader.DownloadAsync(sourceUrl, NewDir(), "mb-fresh");

        Assert.That(second.Success, Is.True, second.ErrorMessage);
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

        var sourceUrl = SlskdSearchProvider.BuildSourceUrl(
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
            SlskdSearchProvider.BuildSourceUrl("peer", "Music\\Artist - Song.mp3", 123), output, "mb-fail");

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
            SlskdSearchProvider.BuildSourceUrl("peer", "Music\\Artist - Song.mp3", 123), output, "mb-queued");

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
            SlskdSearchProvider.BuildSourceUrl("peer", "Music\\Artist - Song.mp3", 123), output, "mb-nofile");

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
            SlskdSearchProvider.BuildSourceUrl("peer", "Artist - Song.flac", 15), output, "mb-junk");

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
        var url = SlskdSearchProvider.BuildSourceUrl(username, filename, size);
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

    [TestCase("Completed, Succeeded", true)]
    [TestCase("Completed, Errored", true)]
    [TestCase("Queued", false)]
    [TestCase("InProgress", false)]
    [TestCase("Completed", true)]
    public void TransferIsTerminal_KnowsTheStates(string state, bool expected)
        => Assert.That(SlskdDownloader.TransferIsTerminal(state), Is.EqualTo(expected));
}
