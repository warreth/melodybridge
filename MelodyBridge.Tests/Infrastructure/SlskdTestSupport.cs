using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Shared stubbed slskd HTTP boundary and payload shapes for the downloader
/// and search provider tests. No live network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
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

internal static class SlskdTestSupport
{
    public static SlskdDownloader Create(
        StubHttpMessageHandler handler, string? downloadsPath = null, StubPluginConfig? config = null)
    {
        config ??= new StubPluginConfig();
        config.Set("slskd", "api_key", "test-key-0123456789");
        config.Set("slskd", "downloads_path", downloadsPath ?? "/tmp/does-not-matter");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030") };
        return new SlskdDownloader(http, config, NullLogger<SlskdDownloader>.Instance)
        {
            TransferPollInterval = TimeSpan.FromMilliseconds(10),
            TransferTimeout = TimeSpan.FromSeconds(2),
        };
    }

    public static SlskdSearchProvider CreateProvider(StubHttpMessageHandler handler)
    {
        var config = new StubPluginConfig();
        config.Set("slskd", "api_key", "test-key-0123456789");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5030") };
        var session = new SlskdSession(http, config, NullLogger<SlskdSession>.Instance);
        return new SlskdSearchProvider(session, NullLogger<SlskdSearchProvider>.Instance)
        {
            SearchPollInterval = TimeSpan.FromMilliseconds(10),
            SearchPollTimeout = TimeSpan.FromSeconds(2),
        };
    }

    public static HttpResponseMessage Json(HttpStatusCode code, object body) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Ok(object body) => Json(HttpStatusCode.OK, body);

    public static object SlskFile(string filename, long size, int? bitRate = null,
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

    public static object Response(string username, bool freeSlot, params object[] files) => new
    {
        username,
        token = 1,
        hasFreeUploadSlot = freeSlot,
        uploadSpeed = 1_000_000,
        queueLength = 0,
        fileCount = files.Length,
        files,
    };
}
