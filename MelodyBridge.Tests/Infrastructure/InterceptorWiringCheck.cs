using System.Data.Common;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Wiring check: does an EF Core command interceptor registered on the
/// options actually fire for a plain query in this project's SQLite setup?
/// </summary>
[TestFixture]
public class InterceptorWiringCheck
{
    [Test]
    public async Task Observer()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-wire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "wire.db");
        var spy = new Spy(dbPath);
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .AddInterceptors(spy)
            .Options;

        await using (var db = new MelodyBridgeDbContext(options))
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity { Id = "p1", Name = "Wire", SourceUrl = "stub:1" });
            db.SaveChanges();
            _ = await db.Playlists.AsNoTracking().ToListAsync();
        }

        Assert.That(spy.ReaderCommands, Is.GreaterThan(0), "the spy saw reader commands");
        Directory.Delete(dir, recursive: true);
    }

    private sealed class Spy(string dbPath) : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }
        public int NonQueryCommands { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            return new ValueTask<DbDataReader>(result);
        }

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            ReaderCommands++;
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            NonQueryCommands++;
            return new ValueTask<int>(result);
        }
    }
}
