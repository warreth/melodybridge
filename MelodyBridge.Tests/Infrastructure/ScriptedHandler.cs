using System.Net;
using System.Text;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Scripted HTTP boundary for media-server tests: records every request and
/// answers from a route table. Tests assert on the exact URLs that were (or
/// were not) called — no mocks of the sync classes themselves. Each match
/// serves a fresh response, so a route may answer many requests.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    public List<(string Method, string Url)> Requests { get; } = new();

    private readonly List<(string PathContains, Func<string, HttpResponseMessage> Respond)> _routes = new();

    /// <summary>Registers a JSON body for every request whose URL contains the path.</summary>
    public ScriptedHandler On(string pathContains, string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
        => On(pathContains, _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        });

    /// <summary>Registers a per-request response builder for the path.</summary>
    public ScriptedHandler On(string pathContains, Func<string, HttpResponseMessage> respond)
    {
        _routes.Add((pathContains, respond));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.PathAndQuery;
        Requests.Add((request.Method.Method, url));
        // Longest route wins (so "/a/b" is not swallowed by "/a"); among
        // equally long matches the latest registration wins, letting tests
        // override shared route sets.
        var match = _routes
            .Select((route, index) => (route, index))
            .Where(entry => url.Contains(entry.route.PathContains, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.route.PathContains.Length)
            .ThenByDescending(entry => entry.index)
            .FirstOrDefault();
        if (match != default)
            return Task.FromResult(match.route.Respond(url));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }

    public static string Json(string body) => body;
}
