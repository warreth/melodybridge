using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MelodyBridge.Infrastructure.Data;

/// <summary>
/// SQLite PRAGMAs that matter for this app's write load. WAL already lets
/// readers and the single writer proceed together; <c>synchronous=NORMAL</c>
/// drops the extra fsync per commit that FULL performs (WAL keeps the file
/// consistent, so a power cut can lose the last commit but never corrupt the
/// database), and a larger page cache keeps hot indexes in memory while the
/// scanner and a download run touch the same tables. The PRAGMA is per
/// connection, so it runs when a connection opens.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA synchronous=NORMAL; PRAGMA cache_size=-20000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = Pragmas;
            command.ExecuteNonQuery();
        }
        catch
        {
            // A pragma is an optimisation: a connection that refuses one still
            // works, and failing the open over it would take the app down.
        }
    }
}
