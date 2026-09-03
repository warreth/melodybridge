using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Application.Services;

public class SyncEngine
{
    private readonly MelodyBridgeDbContext _db;
    private readonly M3uGenerator _m3u;
    private readonly IEnumerable<IMediaServerSync> _servers;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(MelodyBridgeDbContext db, M3uGenerator m3u, IEnumerable<IMediaServerSync> servers, ILogger<SyncEngine> logger)
    {
        _db = db;
        _m3u = m3u;
        _servers = servers;
        _logger = logger;
    }

    public async Task<string> GenerateM3uForPlaylistAsync(Playlist playlist, IEnumerable<ScanLocation> searchLocations, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        return await _m3u.GenerateM3uAsync(playlist, searchLocations, options, ct);
    }

    public async Task SyncToServerAsync(Playlist playlist, PlaylistOutputOptions options, string serverName, CancellationToken ct = default)
    {
        var server = _servers.FirstOrDefault(s => s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server == null) throw new InvalidOperationException($"Server plugin '{serverName}' not found");
        await server.SyncPlaylistAsync(playlist, options, ct);
    }

    public async Task<MediaServerSyncReport?> SyncToServerWithReportAsync(Playlist playlist, PlaylistOutputOptions options, string serverName, CancellationToken ct = default)
    {
        var server = _servers.FirstOrDefault(s => s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (server == null) throw new InvalidOperationException($"Server plugin '{serverName}' not found");
        await server.SyncPlaylistAsync(playlist, options, ct);

        // If the server exposes a report, return it
        return server.LastReport;
    }
}
