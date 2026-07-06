using MelodyBridge.Core;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.IO;

namespace MelodyBridge.Infrastructure.MediaServers;

public class JellyfinSync : IMediaServerSync
{
    private readonly HttpClient _http;
    private readonly ILogger<JellyfinSync> _logger;
    public string Name => "Jellyfin";

    private MediaServerSyncReport? _lastReport;

    public JellyfinSync(HttpClient http, ILogger<JellyfinSync> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        if (playlist?.Name == null) throw new ArgumentException("Playlist needs a name");

        var itemIds = new List<string>();
        var unresolved = new List<string>();

        if (playlist.Tracks != null)
        {
            foreach (var track in playlist.Tracks)
            {
                try
                {
                    var path = track.CurrentTrackLocation?.Path;
                    if (string.IsNullOrEmpty(path))
                    {
                        _logger.LogDebug("No path available for track {title}, skipping lookup", track.Title);
                        continue;
                    }

                    var lookupPath = path;
                    if (options.PathRemap != null)
                    {
                        foreach (var kv in options.PathRemap)
                        {
                            if (lookupPath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                lookupPath = kv.Value + lookupPath.Substring(kv.Key.Length);
                                break;
                            }
                        }
                    }

                    // 1) Try lookup by exact path
                    var encoded = System.Net.WebUtility.UrlEncode(lookupPath);
                    var byPathUrl = $"/Items/ByPath?path={encoded}";
                    try
                    {
                        var resp = await _http.GetAsync(byPathUrl, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            var item = await resp.Content.ReadFromJsonAsync<JellyfinItem>(cancellationToken: ct);
                            if (item != null && !string.IsNullOrEmpty(item.Id))
                            {
                                itemIds.Add(item.Id);
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "ByPath lookup failed for {path}", lookupPath);
                    }

                    // 2) Try search by full path
                    try
                    {
                        var searchUrl = $"/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path&SearchTerm={System.Net.WebUtility.UrlEncode(lookupPath)}";
                        var resp2 = await _http.GetAsync(searchUrl, ct);
                        if (resp2.IsSuccessStatusCode)
                        {
                            var list = await resp2.Content.ReadFromJsonAsync<JellyfinItemsResult>(cancellationToken: ct);
                            if (list?.Items != null && list.Items.Length > 0)
                            {
                                itemIds.Add(list.Items[0].Id);
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Search lookup failed for {path}", lookupPath);
                    }

                    // 3) Try search by filename only
                    try
                    {
                        var fileName = Path.GetFileName(lookupPath);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            var searchUrl2 = $"/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path&SearchTerm={System.Net.WebUtility.UrlEncode(fileName)}";
                            var resp3 = await _http.GetAsync(searchUrl2, ct);
                            if (resp3.IsSuccessStatusCode)
                            {
                                var list2 = await resp3.Content.ReadFromJsonAsync<JellyfinItemsResult>(cancellationToken: ct);
                                if (list2?.Items != null && list2.Items.Length > 0)
                                {
                                    itemIds.Add(list2.Items[0].Id);
                                    continue;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Filename search failed for {path}", lookupPath);
                    }

                    // 4) Try search by metadata (title/artist)
                    try
                    {
                        var terms = new List<string>();
                        if (!string.IsNullOrEmpty(track.Title)) terms.Add(track.Title!);
                        if (!string.IsNullOrEmpty(track.Artist)) terms.Add(track.Artist!);
                        if (terms.Count > 0)
                        {
                            var term = System.Net.WebUtility.UrlEncode(string.Join(' ', terms));
                            var searchUrl3 = $"/Items?Recursive=true&IncludeItemTypes=Audio&Fields=Path&SearchTerm={term}";
                            var resp4 = await _http.GetAsync(searchUrl3, ct);
                            if (resp4.IsSuccessStatusCode)
                            {
                                var list3 = await resp4.Content.ReadFromJsonAsync<JellyfinItemsResult>(cancellationToken: ct);
                                if (list3?.Items != null && list3.Items.Length > 0)
                                {
                                    itemIds.Add(list3.Items[0].Id);
                                    continue;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Metadata search failed for {path}", lookupPath);
                    }

                    _logger.LogInformation("Could not resolve item for path {path}", lookupPath);
                    unresolved.Add(lookupPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve track in Jellyfin");
                }
            }
        }

        var payload = new
        {
            Name = playlist.Name,
            PlaylistItems = itemIds.Select(id => new { Id = id }).ToArray()
        };

        try
        {
            var existingResp = await _http.GetAsync($"/Playlists?Name={System.Net.WebUtility.UrlEncode(playlist.Name)}", ct);
            if (existingResp.IsSuccessStatusCode)
            {
                var existing = await existingResp.Content.ReadFromJsonAsync<JellyfinPlaylistsResult>(cancellationToken: ct);
                var match = existing?.Items?.FirstOrDefault(p => string.Equals(p.Name, playlist.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    _logger.LogInformation("Updating existing playlist {name}", playlist.Name);
                    // Try updating in-place first
                    try
                    {
                        var putResp = await _http.PutAsJsonAsync($"/Playlists/{match.Id}", payload, ct);
                        if (putResp.IsSuccessStatusCode)
                        {
                            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(), match.Id, "Updated existing playlist");
                            return;
                        }
                        else
                        {
                            _logger.LogDebug("PUT update failed, status {status}", putResp.StatusCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "PUT update failed for playlist {name}", playlist.Name);
                    }

                    // Fallback: delete and recreate
                    try { await _http.DeleteAsync($"/Playlists/{match.Id}", ct); } catch { }
                }
            }

            var resp = await _http.PostAsJsonAsync("/Playlists", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jellyfin returned {status} when creating playlist {name}", resp.StatusCode, playlist.Name);
            }
            else
            {
                // Try to extract created playlist id
                try
                {
                    var created = await resp.Content.ReadFromJsonAsync<JellyfinPlaylist>(cancellationToken: ct);
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(), created?.Id, "Created playlist");
                }
                catch
                {
                    _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(), null, "Created playlist (no id)");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Jellyfin API to create playlist");
            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(), null, $"Error: {ex.Message}");
        }

        // If we reach here without setting _lastReport, set a default one
        if (_lastReport == null)
        {
            _lastReport = new MediaServerSyncReport(itemIds.Count, unresolved.ToArray(), null, "Completed with warnings");
        }
    }

    public MediaServerSyncReport? GetLastReport() => _lastReport;

    private class JellyfinItem { public string? Id { get; set; } }
    private class JellyfinItemsResult { public JellyfinItem[]? Items { get; set; } }
    private class JellyfinPlaylist { public string? Id { get; set; } public string? Name { get; set; } }
    private class JellyfinPlaylistsResult { public JellyfinPlaylist[]? Items { get; set; } }
}

