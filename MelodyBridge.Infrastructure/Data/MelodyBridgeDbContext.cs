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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrackEntity>().HasKey(t => t.Id);
        modelBuilder.Entity<TrackEntity>().HasIndex(t => t.MelodyId).IsUnique();
        modelBuilder.Entity<PlaylistEntity>().HasKey(p => p.Id);
        modelBuilder.Entity<ProviderStateRow>().HasKey(ps => ps.ProviderId);
        modelBuilder.Entity<ProviderStateRow>().Property(ps => ps.ProviderId).HasMaxLength(64);
    }
}

public class TrackEntity
{
    public int Id { get; set; }
    public string? MelodyId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? MediaType { get; set; }
    public string? CurrentPath { get; set; }
}

public class PlaylistEntity
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? SourceIdentifier { get; set; }
}

/// <summary>
/// Stores the enabled/disabled state for each music provider plugin.
/// </summary>
public class ProviderStateRow
{
    public string ProviderId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
