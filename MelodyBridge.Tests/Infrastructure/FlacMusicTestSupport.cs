using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Shared HTTP and payload fixtures for the FLAC Music client, provider and
/// downloader tests. No live network.
/// </summary>
internal sealed class FlacScriptedHandler : HttpMessageHandler
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

internal static class FlacMusicTestSupport
{
    public static HttpResponseMessage Json(
        string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] data)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    public static FlacMusicClient Client(FlacScriptedHandler handler)
        => new(new HttpClient(handler), NullLogger<FlacMusicClient>.Instance);

    public static FlacMusicDownloader Downloader(
        FlacScriptedHandler handler, StubPluginConfig? config = null)
    {
        var client = Client(handler);
        return new FlacMusicDownloader(
            client, config ?? new StubPluginConfig(),
            NullLogger<FlacMusicDownloader>.Instance);
    }

    public static FlacMusicSearchProvider Provider(
        FlacScriptedHandler handler, StubPluginConfig? config = null)
    {
        var client = Client(handler);
        var session = new FlacMusicSession(
            client, config ?? new StubPluginConfig(),
            NullLogger<FlacMusicSession>.Instance);
        return new FlacMusicSearchProvider(
            session, NullLogger<FlacMusicSearchProvider>.Instance);
    }

    public static object Format(string format, int bitrate) => new { format, bitrate };

    public static object Track(
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

    public static string SearchJson(params object[] tracks)
        => JsonSerializer.Serialize(new
        {
            code = 0,
            msg = "ok",
            data = new { list = tracks, total = tracks.Length },
        });
}
