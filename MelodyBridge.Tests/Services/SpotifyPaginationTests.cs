using System.Net;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The public Spotify paging loop must terminate on the response's own
/// page size and on an empty page. The old loop stepped a fixed 100 and
/// only stopped when the parsed count reached the advertised total, so
/// a clamped page set skipped tracks and a filtered or empty page made
/// it request pages forever.
/// </summary>
[TestFixture]
public class SpotifyPaginationTests
{
    private sealed class PagingHandler : HttpMessageHandler
    {
        private readonly int _total;
        private readonly int _pageCap;
        private readonly int _maxApiRequests;
        private readonly bool _emptyAfterFirstPage;

        /// <summary>The offset of every /items request, in order.</summary>
        public List<int> ItemOffsets { get; } = new();

        public PagingHandler(int total, int pageCap, int maxApiRequests, bool emptyAfterFirstPage = false)
        {
            _total = total;
            _pageCap = pageCap;
            _maxApiRequests = maxApiRequests;
            _emptyAfterFirstPage = emptyAfterFirstPage;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "open.spotify.com")
            {
                // The embedding page carries the anonymous API token.
                return Json("""<html><script>{"accessToken":"tok"}</script></html>""");
            }
            if (uri.AbsolutePath.Contains("/items"))
            {
                var offset = int.Parse(HttpUtility.ParseQueryString(uri.Query)["offset"] ?? "0");
                ItemOffsets.Add(offset);
                if (ItemOffsets.Count > _maxApiRequests)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{}"),
                    });

                var count = _emptyAfterFirstPage && offset > 0
                    ? 0
                    : Math.Clamp(_total - offset, 0, _pageCap);
                var items = string.Join(",", Enumerable.Range(offset, count).Select(i =>
                    "{\"item\":{\"id\":\"id" + i + "\",\"name\":\"Track " + i
                    + "\",\"duration_ms\":180000,\"artists\":[{\"name\":\"Artist\"}]}}"));
                return Json("{\"total\":" + _total + ",\"items\":[" + items + "]}");
            }
            if (uri.Host == "api.spotify.com" && uri.AbsolutePath.Contains("/playlists/"))
            {
                return Json("""{"name":"Paged","owner":{"display_name":"owner"},"images":[]}""");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static SpotifySourceProvider ProviderFor(PagingHandler handler)
        => new(NullLogger<SpotifySourceProvider>.Instance, null, new HttpClient(handler));

    private const string PlaylistUrl = "https://open.spotify.com/playlist/2O9RpWPbupc8NrX4T5ZXia";

    [Test]
    public async Task ClampedPages_DoNotSkipTracks_AndTerminate()
    {
        // Spotify answers with 50 items per page although 100 were asked.
        var handler = new PagingHandler(total: 101, pageCap: 50, maxApiRequests: 3);

        var playlist = await ProviderFor(handler).GetPlaylistAsync(PlaylistUrl);

        Assert.That(playlist.Tracks, Has.Count.EqualTo(101),
            "every track must come back even when the page size is clamped");
        Assert.That(handler.ItemOffsets, Is.EqualTo(new[] { 0, 50, 100 }),
            "the cursor advances by the page the API returned");
    }

    [Test]
    public async Task EmptyPage_EndsTheLoop_AndThePartialWarningStaysHonest()
    {
        // Two items on the first page, then nothing, while total says 5.
        var handler = new PagingHandler(total: 5, pageCap: 2, maxApiRequests: 2, emptyAfterFirstPage: true);

        var playlist = await ProviderFor(handler).GetPlaylistAsync(PlaylistUrl);

        Assert.That(handler.ItemOffsets, Is.EqualTo(new[] { 0, 2 }),
            "an empty page ends the fetch instead of asking another offset");
        Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
        Assert.That(playlist.Warning, Is.Not.Null,
            "fewer tracks than advertised must stay visible as a partial fetch");
    }
}
