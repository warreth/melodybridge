using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.FlacMusic;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The flac.music.hi.cn fallback: its search and getUrl shapes, the format
/// picker, URL round-tripping and a real download through a scripted HTTP
/// boundary. No live network.
/// </summary>
[TestFixture]
public class FlacMusicDownloaderTests
{
    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        _tempDirs.Clear();
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-flacmusic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Url, string? Body)> Requests { get; } = new();
        public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } =
            (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.AbsoluteUri, body));
            return Respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] data)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static FlacMusicClient Client(ScriptedHandler handler)
        => new(new HttpClient(handler), NullLogger<FlacMusicClient>.Instance);

    private static FlacMusicDownloader Downloader(
        ScriptedHandler handler, StubPluginConfig? config = null)
    {
        var client = Client(handler);
        return new FlacMusicDownloader(
            client, config ?? new StubPluginConfig(),
            NullLogger<FlacMusicDownloader>.Instance);
    }

    private static object Format(string format, int bitrate) => new { format, bitrate };

    private static object Track(
        int id, string name, string artist, int duration,
        string time = "1793052853", string sign = "abc")
        => new
        {
            id,
            name,
            artist,
            album_name = "Album",
            duration = duration.ToString(),
            minfo = new[]
            {
                Format("flac", 2000),
                Format("mp3", 320),
                Format("mp3", 128),
            },
            time,
            sign,
            link = "https://www.kuwo.cn/play_detail/" + id,
            pic_url = "https://img.test/cover.jpg",
        };

    private static string SearchJson(params object[] tracks)
        => JsonSerializer.Serialize(new
        {
            code = 0,
            msg = "ok",
            data = new { list = tracks, total = tracks.Length },
        });

    private static byte[] FlacBytes()
    {
        var bytes = new byte[4096];
        "fLaC"u8.CopyTo(bytes);
        for (var i = 4; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    [Test]
    public void ParseSearch_ReadsTheKuwoShape()
    {
        var result = FlacMusicClient.ParseSearch(SearchJson(
            Track(336887, "Back In Black", "AC/DC", 255)), "kuwo");

        Assert.That(result.Status, Is.EqualTo(FlacMusicClient.Status.Ok));
        Assert.That(result.Tracks, Has.Count.EqualTo(1));
        var track = result.Tracks[0];
        Assert.That(track.Id, Is.EqualTo("336887"));
        Assert.That(track.Title, Is.EqualTo("Back In Black"));
        Assert.That(track.Artist, Is.EqualTo("AC/DC"));
        Assert.That(track.Duration, Is.EqualTo(TimeSpan.FromSeconds(255)));
        Assert.That(track.Formats.Select(f => (f.Name, f.BitrateKbps)),
            Is.EquivalentTo(new[] { ("flac", 2000), ("mp3", 320), ("mp3", 128) }));
        Assert.That(track.Time, Is.EqualTo("1793052853"));
        Assert.That(track.Sign, Is.EqualTo("abc"));
    }

    [Test]
    public void ParseSearch_KeepsTheSiteErrorText()
    {
        var result = FlacMusicClient.ParseSearch(
            """{"code":-1,"msg":"不支持的平台"}""", "qq");

        Assert.That(result.Status, Is.EqualTo(FlacMusicClient.Status.Failed));
        Assert.That(result.Message, Is.EqualTo("不支持的平台"));
    }

    [Test]
    public void ParseGetUrl_ReadsTheDirectAudioUrl()
    {
        var result = FlacMusicClient.ParseGetUrl("""
        {"code":0,"msg":"ok","data":{"url":"https://cdn.test/a.flac","format":"flac","bitrate":2000,"songid":336887}}
        """);

        Assert.That(result.Status, Is.EqualTo(FlacMusicClient.Status.Ok));
        Assert.That(result.Url, Is.EqualTo("https://cdn.test/a.flac"));
        Assert.That(result.Format, Is.EqualTo("flac"));
        Assert.That(result.BitrateKbps, Is.EqualTo(2000));
    }

    [Test]
    public void ParseGetUrl_ExpiredSignIsItsOwnStatus()
    {
        var result = FlacMusicClient.ParseGetUrl("""{"code":-3,"msg":"请求已过期"}""");

        Assert.That(result.Status, Is.EqualTo(FlacMusicClient.Status.Expired));
    }

    [Test]
    public void ParseSizeBytes_HandlesTheSitesMegabyteStrings()
    {
        using var doc = JsonDocument.Parse("\"31.08Mb\"");
        Assert.That(FlacMusicClient.ParseSizeBytes(doc.RootElement),
            Is.EqualTo((long)(31.08 * 1024 * 1024)));
    }

    // ── Format choice ────────────────────────────────────────────────────

    private static readonly FlacMusicClient.Format[] AllFormats =
    {
        new("flac", 2000),
        new("mp3", 320),
        new("mp3", 128),
    };

    [Test]
    public void SelectFormat_FollowsTheQualityBand()
    {
        Assert.That(FlacMusicDownloader.SelectFormat(AllFormats,
                new DownloadQuality(AudioFormat.Flac))!.Name, Is.EqualTo("flac"));
        Assert.That(FlacMusicDownloader.SelectFormat(AllFormats,
                new DownloadQuality(AudioFormat.Mp3, 200, 320))!.BitrateKbps, Is.EqualTo(320));
        Assert.That(FlacMusicDownloader.SelectFormat(AllFormats,
                new DownloadQuality(AudioFormat.Mp3, null, 160))!.BitrateKbps, Is.EqualTo(128));
        Assert.That(FlacMusicDownloader.SelectFormat(AllFormats,
                new DownloadQuality(AudioFormat.Auto, null, 320))!.BitrateKbps, Is.EqualTo(320));
        Assert.That(FlacMusicDownloader.SelectFormat(AllFormats,
                new DownloadQuality(AudioFormat.Auto, null, null))!.Name, Is.EqualTo("flac"));
        Assert.That(FlacMusicDownloader.SelectFormat(AllFormats,
                new DownloadQuality(AudioFormat.Aac)), Is.Null);
    }

    [Test]
    public void CleanTitle_RemovesTheChineseDescriptionSuffix()
    {
        Assert.That(FlacMusicDownloader.CleanTitle(
                "Shoot to Thrill-《钢铁侠2》电影主题曲"), Is.EqualTo("Shoot to Thrill"));
        Assert.That(FlacMusicDownloader.CleanTitle("Back In Black"),
            Is.EqualTo("Back In Black"));
    }

    [Test]
    public void SourceUrlRoundTripsTheSignAndFormats()
    {
        var track = new FlacMusicClient.Track(
            "kuwo", "336887", "Back In Black", "AC/DC", "Back In Black",
            TimeSpan.FromSeconds(255), AllFormats, "1793052853", "abc", null, null);

        var url = FlacMusicDownloader.EncodeSourceUrl(track, "Back In Black", DownloadQuality.Any);

        Assert.That(FlacMusicDownloader.TryParseSourceUrl(url, out var link), Is.True);
        Assert.That(link.Platform, Is.EqualTo("kuwo"));
        Assert.That(link.Id, Is.EqualTo("336887"));
        Assert.That(link.Time, Is.EqualTo("1793052853"));
        Assert.That(link.Sign, Is.EqualTo("abc"));
        Assert.That(link.Formats.Select(f => f.BitrateKbps),
            Is.EquivalentTo(new[] { 2000, 320, 128 }));
        Assert.That(link.Artist, Is.EqualTo("AC/DC"));
        Assert.That(link.Title, Is.EqualTo("Back In Black"));
        Assert.That(link.Duration, Is.EqualTo(TimeSpan.FromSeconds(255)));
    }

    [Test]
    public void DirectKuwoAndNetEaseLinksAreRecognized()
    {
        Assert.That(FlacMusicDownloader.TryParseSourceUrl(
            "https://www.kuwo.cn/play_detail/336887", out var kuwo), Is.True);
        Assert.That(kuwo.Platform, Is.EqualTo("kuwo"));
        Assert.That(kuwo.Id, Is.EqualTo("336887"));

        Assert.That(FlacMusicDownloader.TryParseSourceUrl(
            "https://music.163.com/#/song?id=3880720", out var netease), Is.True);
        Assert.That(netease.Platform, Is.EqualTo("wyy"));
        Assert.That(netease.Id, Is.EqualTo("3880720"));
    }

    // ── Search ───────────────────────────────────────────────────────────

    [Test]
    public async Task SearchAsync_PicksTheFormatForTheRequestedQuality()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = (_, body) =>
        {
            var platform = body is not null && body.Contains("platform=wyy") ? "wyy" : "kuwo";
            return Json(SearchJson(
                Track(platform == "kuwo" ? 336887 : 3880720,
                    "Back In Black", "AC/DC", 255)));
        };
        var downloader = Downloader(handler);

        var high = await downloader.SearchAsync(new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Auto, null, 320)));
        var saver = await downloader.SearchAsync(new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Auto, null, 160)));
        var lossless = await downloader.SearchAsync(new TrackQuery(
            "AC/DC", "Back In Black", new DownloadQuality(AudioFormat.Flac)));

        Assert.That(high, Is.Not.Null);
        Assert.That(high!.BitrateKbps, Is.EqualTo(320));
        Assert.That(saver!.BitrateKbps, Is.EqualTo(128));
        Assert.That(lossless!.BitrateKbps, Is.Null);
        Assert.That(lossless.SourceUrl, Does.Contain("flac%3A2000"));
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task DownloadAsync_FollowsGetUrlAndSniffsTheFile()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = (request, body) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("act=getUrl"))
                return Json("""{"code":0,"msg":"ok","data":{"url":"https://cdn.test/a.flac"}}""");
            if (url == "https://cdn.test/a.flac")
                return Bytes(FlacBytes());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Downloader(handler);
        var track = new FlacMusicClient.Track(
            "kuwo", "336887", "Back In Black", "AC/DC", "Back In Black",
            TimeSpan.FromSeconds(255), AllFormats, "1793052853", "abc", null, null);
        var sourceUrl = FlacMusicDownloader.EncodeSourceUrl(track, "Back In Black", DownloadQuality.Any);

        var result = await downloader.DownloadAsync(
            sourceUrl, NewDir(), "melody-1", new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.FilePath, Does.EndWith(".flac"));
        Assert.That(File.ReadAllBytes(result.FilePath!)[..4], Is.EqualTo("fLaC"u8.ToArray()));
    }

    [Test]
    public async Task DownloadAsync_RefreshesAnExpiredSignByIdWithoutTextSearch()
    {
        using var handler = new ScriptedHandler();
        var getUrlCalls = 0;
        var songCalls = 0;
        handler.Respond = (request, body) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("act=getUrl"))
            {
                getUrlCalls++;
                return getUrlCalls == 1
                    ? Json("""{"code":-3,"msg":"request expired"}""")
                    : Json("""{"code":0,"msg":"ok","data":{"url":"https://cdn.test/fresh.flac"}}""");
            }
            if (url.Contains("act=song"))
            {
                songCalls++;
                return Json(SearchJson(
                    Track(336887, "Back In Black", "AC/DC", 255,
                        time: "1793059999", sign: "fresh")));
            }
            if (url == "https://cdn.test/fresh.flac")
                return Bytes(FlacBytes());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Downloader(handler);
        var track = new FlacMusicClient.Track(
            "kuwo", "336887", "Back In Black", "AC/DC", "Back In Black",
            TimeSpan.FromSeconds(255), AllFormats, "1793052853", "old", null, null);
        var sourceUrl = FlacMusicDownloader.EncodeSourceUrl(track, "Back In Black", DownloadQuality.Any);

        var result = await downloader.DownloadAsync(
            sourceUrl, NewDir(), "melody-2", new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(getUrlCalls, Is.EqualTo(2));
        Assert.That(songCalls, Is.EqualTo(1),
            "the refresh reads the same song by id, never a text search");
        var refresh = handler.Requests.Single(r => r.Url.Contains("act=song"));
        Assert.That(refresh.Body, Does.Contain("id=336887"));
        Assert.That(refresh.Body, Does.Not.Contain("keyword"));
    }

    [Test]
    public async Task DownloadAsync_ExplicitMp3RejectsAnAacStreamFromTheSite()
    {
        using var handler = new ScriptedHandler();
        var cdnCalls = 0;
        handler.Respond = (request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("act=getUrl"))
                return Json("""
                {"code":0,"msg":"ok","data":{"url":"https://cdn.test/a.aac","format":"aac","bitrate":100}}
                """);
            if (url == "https://cdn.test/a.aac")
            {
                cdnCalls++;
                return Bytes(FlacBytes());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Downloader(handler);
        var track = new FlacMusicClient.Track(
            "kuwo", "336887", "Back In Black", "AC/DC", "Back In Black",
            TimeSpan.FromSeconds(255), AllFormats, "1793052853", "abc", null, null);
        var sourceUrl = FlacMusicDownloader.EncodeSourceUrl(track, "Back In Black", DownloadQuality.Any);

        var result = await downloader.DownloadAsync(
            sourceUrl, NewDir(), "melody-mp3", new DownloadQuality(AudioFormat.Mp3, null, 160));

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("served aac"));
        Assert.That(cdnCalls, Is.Zero, "the mismatch is rejected before the audio is fetched");
    }

    [Test]
    public async Task DownloadAsync_AutoQualityAcceptsTheAacTier()
    {
        using var handler = new ScriptedHandler();
        handler.Respond = (request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("act=getUrl"))
                return Json("""
                {"code":0,"msg":"ok","data":{"url":"https://cdn.test/a.aac","format":"aac","bitrate":100}}
                """);
            if (url == "https://cdn.test/a.aac")
                return Bytes(FlacBytes());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var downloader = Downloader(handler);
        var track = new FlacMusicClient.Track(
            "kuwo", "336887", "Back In Black", "AC/DC", "Back In Black",
            TimeSpan.FromSeconds(255), AllFormats, "1793052853", "abc", null, null);
        var sourceUrl = FlacMusicDownloader.EncodeSourceUrl(track, "Back In Black", DownloadQuality.Any);

        var result = await downloader.DownloadAsync(
            sourceUrl, NewDir(), "melody-auto", new DownloadQuality(AudioFormat.Auto, null, 160));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public async Task DownloadAsync_RejectsUnsupportedQualityAndForeignUrls()
    {
        using var handler = new ScriptedHandler();
        var downloader = Downloader(handler);

        var foreign = await downloader.DownloadAsync(
            "https://example.test/nothing", NewDir(), "melody-3");
        Assert.That(foreign.Success, Is.False);
        Assert.That(foreign.ErrorMessage, Does.Contain("not a FLAC Music"));

        var track = new FlacMusicClient.Track(
            "kuwo", "336887", "Back In Black", "AC/DC", null,
            TimeSpan.FromSeconds(255), AllFormats, "1793052853", "abc", null, null);
        var sourceUrl = FlacMusicDownloader.EncodeSourceUrl(track, "Back In Black", DownloadQuality.Any);
        var aac = await downloader.DownloadAsync(
            sourceUrl, NewDir(), "melody-4", new DownloadQuality(AudioFormat.Aac));

        Assert.That(aac.Success, Is.False);
        Assert.That(aac.ErrorMessage, Does.Contain("cannot serve"));
    }
}
