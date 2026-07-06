using Microsoft.AspNetCore.Mvc;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;

namespace MelodyBridge.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SyncController : ControllerBase
{
    private readonly SyncEngine _engine;
    private readonly ILogger<SyncController> _logger;

    public SyncController(SyncEngine engine, ILogger<SyncController> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public record RunSyncRequest(Playlist Playlist, string ServerName, PlaylistOutputOptions Options);

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] RunSyncRequest req, CancellationToken ct)
    {
        if (req.Playlist == null) return BadRequest("Missing playlist");
        try
        {
            var report = await _engine.SyncToServerWithReportAsync(req.Playlist, req.Options, req.ServerName, ct);
            if (report == null) return Ok(new { Message = "Sync completed (no detailed report)" });
            return Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
