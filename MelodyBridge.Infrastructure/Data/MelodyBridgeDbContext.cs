using Microsoft.EntityFrameworkCore;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Data;

public class MelodyBridgeDbContext : DbContext
{
    public MelodyBridgeDbContext(DbContextOptions<MelodyBridgeDbContext> options) : base(options)
    {
    }

    public DbSet<TrackEntity> Tracks { get; set; }
    public DbSet<PlaylistEntity> Playlists { get; set; }
    public DbSet<ProviderStateRow> ProviderStates { get; set; }
    public DbSet<SourceEntity> Sources { get; set; }
    public DbSet<SyncJobEntity> SyncJobs { get; set; }
    public DbSet<SyncJobRunEntity> SyncJobRuns { get; set; }
    public DbSet<ScanLocationEntity> ScanLocations { get; set; }
    public DbSet<DownloaderSettingEntity> DownloaderSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrackEntity>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.MelodyId).IsUnique();
            e.HasIndex(t => new { t.ExternalPlatform, t.ExternalId });
            e.Property(t => t.MelodyId).HasMaxLength(64);
            e.Property(t => t.ExternalId).HasMaxLength(128);
            e.Property(t => t.ExternalPlatform).HasMaxLength(64);
            e.Property(t => t.Title).HasMaxLength(512);
            e.Property(t => t.Artist).HasMaxLength(256);
            e.Property(t => t.MediaType).HasMaxLength(32);
            e.Property(t => t.CurrentPath).HasMaxLength(2048);
            e.Property(t => t.SourceUrl).HasMaxLength(2048);
        });

        modelBuilder.Entity<PlaylistEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(256);
            e.Property(p => p.SourceUrl).HasMaxLength(2048);
            e.Property(p => p.Description).HasMaxLength(2048);
            e.Property(p => p.CoverImageUrl).HasMaxLength(2048);
            e.Property(p => p.Owner).HasMaxLength(256);
            e.Property(p => p.ExternalId).HasMaxLength(128);
            e.Property(p => p.TargetDirectory).HasMaxLength(1024);
        });

        modelBuilder.Entity<ProviderStateRow>(e =>
        {
            e.HasKey(ps => ps.ProviderId);
            e.Property(ps => ps.ProviderId).HasMaxLength(64);
        });

        modelBuilder.Entity<SourceEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(64);
            e.Property(s => s.Name).HasMaxLength(256);
            e.Property(s => s.SourceUrl).HasMaxLength(2048);
            e.Property(s => s.TargetDirectory).HasMaxLength(1024);
            e.Property(s => s.Platform).HasMaxLength(64);
        });

        modelBuilder.Entity<SyncJobEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(64);
            e.Property(s => s.Name).HasMaxLength(256);
            e.Property(s => s.SearchLocationPaths).HasMaxLength(4096);
            e.Property(s => s.PathRemapRules).HasMaxLength(8192);
            e.Property(s => s.ExtensionRemapRules).HasMaxLength(2048);
            e.Property(s => s.M3uOutputPath).HasMaxLength(1024);
            e.Property(s => s.JellyfinServerUrl).HasMaxLength(1024);
            e.Property(s => s.JellyfinApiKey).HasMaxLength(512);
            e.Property(s => s.JellyfinUserId).HasMaxLength(128);
            e.Property(s => s.CronExpression).HasMaxLength(256);
        });

        modelBuilder.Entity<SyncJobRunEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.SyncJobId).HasMaxLength(64);
            e.Property(r => r.Message).HasMaxLength(2048);
        });

        modelBuilder.Entity<ScanLocationEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Path).HasMaxLength(2048);
            e.Property(s => s.ScheduleCron).HasMaxLength(256);
        });

        modelBuilder.Entity<DownloaderSettingEntity>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.ProviderId).HasMaxLength(64);
            e.Property(d => d.SettingsJson).HasMaxLength(4096);
        });
    }
}

public class TrackEntity
{
    public int Id { get; set; }
    /// <summary>Stable internal ID written into file tags (MELODY_ID).</summary>
    public string? MelodyId { get; set; }
    /// <summary>ID of the track on its source platform (e.g. Spotify track ID).</summary>
    public string? ExternalId { get; set; }
    /// <summary>Platform the ExternalId belongs to ("Spotify", "YouTubeMusic", ...).</summary>
    public string? ExternalPlatform { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? MediaType { get; set; }
    public int? Bitrate { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>Position of the track inside its playlist snapshot (ordering).</summary>
    public int? Position { get; set; }
    public string? CurrentPath { get; set; }
    public string? SourceUrl { get; set; }
    public string? Platform { get; set; }
    /// <summary>Status of the local file for this track (downloaded / pending / failed).</summary>
    public string? DownloadStatus { get; set; }
    public string? DownloadError { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public int? PlaylistSnapshotId { get; set; }
}

public class PlaylistEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public Platform SourcePlatform { get; set; }
    /// <summary>ID of the track collection on the source platform (e.g. Spotify playlist ID).</summary>
    public string? ExternalId { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? Owner { get; set; }
    public int TrackCount { get; set; }
    public bool AutoSyncEnabled { get; set; }
    public int? AutoSyncIntervalMinutes { get; set; }
    /// <summary>
    /// How re-syncs reconcile local tracks with the source:
    /// Additive — new tracks are added, removed tracks stay.
    /// Mirror — the snapshot exactly matches the source (removed tracks deleted locally).
    /// </summary>
    public string SyncMode { get; set; } = "Additive";
    public DateTime? LastSyncAt { get; set; }
    public SyncStatus LastSyncStatus { get; set; }
    /// <summary>Directory where downloaded files for this playlist are placed.</summary>
    public string? TargetDirectory { get; set; }
    public List<TrackEntity> Tracks { get; set; } = new();
}

public class ProviderStateRow
{
    public string ProviderId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    /// <summary>Waterfall order (lower = tried earlier).</summary>
    public int Priority { get; set; } = int.MaxValue;
}

public class SourceEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = "YouTubeMusic";
    public string SourceUrl { get; set; } = string.Empty;
    public string? TargetDirectory { get; set; }
    public bool AutoSyncEnabled { get; set; }
    public int? AutoSyncIntervalMinutes { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string Status { get; set; } = "Pending";
}

public class SyncJobEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    public string SearchLocationPaths { get; set; } = "[]";
    public string OutputTarget { get; set; } = "M3uFile";
    public string? JellyfinServerUrl { get; set; }
    public string? JellyfinApiKey { get; set; }
    public string? JellyfinUserId { get; set; }
    public string? M3uOutputPath { get; set; }
    public string PathRemapRules { get; set; } = "{}";
    public string ExtensionRemapRules { get; set; } = "{}";
    public string Schedule { get; set; } = "Manual";
    public string? CronExpression { get; set; }
    public string LastRunStatus { get; set; } = "Pending";
    public DateTime? LastRunAt { get; set; }
    public string? LastRunSummary { get; set; }
}

public class SyncJobRunEntity
{
    public int Id { get; set; }
    public string SyncJobId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public string? Message { get; set; }
    public int ResolvedTracks { get; set; }
    public int TotalTracks { get; set; }
}

public class ScanLocationEntity
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public int? ScanIntervalHours { get; set; }
    public string? ScheduleCron { get; set; }
    public bool LiveMonitoring { get; set; }
    public DateTime? LastScannedAt { get; set; }
}

public class DownloaderSettingEntity
{
    public int Id { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? SettingsJson { get; set; }
}
