using MelodyBridge.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace MelodyBridge.Server.Controllers;

/// <summary>
/// Database backup/restore endpoints. All real work happens in
/// <see cref="DatabaseBackupService"/>; the controller only shapes HTTP.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DatabaseController(DatabaseBackupService backup, ILogger<DatabaseController> logger) : ControllerBase
{
    /// <summary>Consistent zip snapshot of the database (VACUUM INTO).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        try
        {
            var bytes = await backup.ExportZipAsync(HttpContext.RequestAborted);
            return File(bytes, "application/zip",
                $"melodybridge_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");
        }
        catch (FileNotFoundException)
        {
            return NotFound("Database file not found");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database export failed");
            return StatusCode(500, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restore from an uploaded zip. The old database is parked as .bak;
    /// a restart is required before the restore takes effect.
    /// </summary>
    [HttpPost("import")]
    [RequestSizeLimit(200_000_000)]
    public async Task<IActionResult> Import(IFormFile file)
    {
        if (file.Length == 0)
            return BadRequest("No file uploaded");
        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only .zip files are accepted");

        try
        {
            await using var stream = file.OpenReadStream();
            await backup.ImportZipAsync(stream, HttpContext.RequestAborted);
            return Ok(new
            {
                Message = "Database restored. Restart MelodyBridge to use it."
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database import failed");
            return StatusCode(500, $"Import failed: {ex.Message}");
        }
    }
}
