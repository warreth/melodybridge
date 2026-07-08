using System.Collections.Concurrent;
using System.IO.Compression;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Server.Controllers;

/// <summary>
/// Controller for database management operations including backup and restore.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DatabaseController : ControllerBase
{
    private readonly MelodyBridgeDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabaseController> _logger;
    private readonly string _databasePath;

    public DatabaseController(
        MelodyBridgeDbContext db,
        IConfiguration config,
        ILogger<DatabaseController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;

        // Get database path from connection string
        var connectionString = config.GetConnectionString("DefaultConnection");
        if (connectionString?.Contains("Data Source=") == true)
        {
            _databasePath = connectionString.Replace("Data Source=", "").Trim();
        }
        else
        {
            // Default path
            _databasePath = Path.Combine(Directory.GetCurrentDirectory(), "melodybridge.db");
        }
    }

    /// <summary>
    /// Export the entire database as a zip file.
    /// Returns the database file compressed as a .zip archive.
    /// </summary>
    /// <returns>Zip file containing melodybridge.db</returns>
    [HttpGet("export")]
    public async Task<IActionResult> ExportDatabase()
    {
        try
        {
            if (!System.IO.File.Exists(_databasePath))
            {
                _logger.LogWarning("Database file not found: {Path}", _databasePath);
                return NotFound("Database file not found");
            }

            var backupDir = Path.Combine(Path.GetDirectoryName(_databasePath) ?? ".", "backups");
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var zipFileName = $"melodybridge_{timestamp}.zip";
            var zipPath = Path.Combine(backupDir, zipFileName);

            // Create zip archive containing the database
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(_databasePath, Path.GetFileName(_databasePath), CompressionLevel.Optimal);
            }

            _logger.LogInformation("Database exported to: {Path}", zipPath);

            var fileBytes = await System.IO.File.ReadAllBytesAsync(zipPath);
            return File(fileBytes, "application/zip", zipFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database export failed");
            return StatusCode(500, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Import/Restore database from a zip file.
    /// Replaces the current database with the backup.
    /// </summary>
    /// <param name="file">The zip file containing the backup</param>
    /// <returns>Success message or error</returns>
    [HttpPost("import")]
    public async Task<IActionResult> ImportDatabase([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded");
        }

        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only .zip files are accepted");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"melodybridge_import_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var zipPath = Path.Combine(tempDir, file.FileName);
            using (var stream = new FileStream(zipPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Extract zip
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            // Find the database file in the extracted contents
            var dbFiles = Directory.GetFiles(tempDir, "melodybridge.db", SearchOption.TopDirectoryOnly);
            if (dbFiles.Length == 0)
            {
                return BadRequest("No database file found in the archive");
            }

            var extractedDbPath = dbFiles[0];

            // Backup current database before restoring
            if (System.IO.File.Exists(_databasePath))
            {
                var backupName = $"melodybridge_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
                var backupPath = Path.Combine(Path.GetDirectoryName(_databasePath) ?? ".", backupName);
                System.IO.File.Move(_databasePath, backupPath);
                _logger.LogInformation("Current database backed up to: {Path}", backupPath);
            }

            // Copy restored database
            System.IO.File.Copy(extractedDbPath, _databasePath, overwrite: true);

            _logger.LogInformation("Database imported successfully from: {Path}", zipPath);

            return Ok(new { Message = "Database imported successfully", DatabasePath = _databasePath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database import failed");
            return StatusCode(500, $"Import failed: {ex.Message}");
        }
        finally
        {
            // Cleanup temp directory
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Get database info including file size and last write time.
    /// </summary>
    /// <returns>Database information</returns>
    [HttpGet("info")]
    public IActionResult GetDatabaseInfo()
    {
        try
        {
            if (!System.IO.File.Exists(_databasePath))
            {
                return NotFound(new { Message = "Database file not found" });
            }

            var fileInfo = new System.IO.FileInfo(_databasePath);

            return Ok(new
            {
                Path = _databasePath,
                SizeBytes = fileInfo.Length,
                SizeMB = Math.Round(fileInfo.Length / (1024.0 * 1024.0), 2),
                LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                LastAccessTimeUtc = fileInfo.LastAccessTimeUtc,
                BackupDirectory = Path.Combine(Path.GetDirectoryName(_databasePath) ?? ".", "backups")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get database info");
            return StatusCode(500, $"Failed to get database info: {ex.Message}");
        }
    }

    /// <summary>
    /// List all available database backups.
    /// </summary>
    /// <returns>List of backup files</returns>
    [HttpGet("backups")]
    public IActionResult ListBackups()
    {
        try
        {
            var backupDir = Path.Combine(Path.GetDirectoryName(_databasePath) ?? ".", "backups");
            Directory.CreateDirectory(backupDir);

            var zipFiles = Directory.GetFiles(backupDir, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(f => new
                {
                    Path = f,
                    Name = Path.GetFileName(f),
                    SizeBytes = new System.IO.FileInfo(f).Length,
                    LastWriteTimeUtc = new System.IO.FileInfo(f).LastWriteTimeUtc
                })
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToList();

            return Ok(new { Count = zipFiles.Count, Backups = zipFiles });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list backups");
            return StatusCode(500, $"Failed to list backups: {ex.Message}");
        }
    }
}
