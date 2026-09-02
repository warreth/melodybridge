namespace MelodyBridge.Server.Services;

/// <summary>
/// Runtime switch for EF Core command logging (the "Executed DbCommand"
/// lines). Default off: database chatter crowds out the few lines that
/// matter on the Logs page. The Advanced page flips it; Program.cs reads
/// the persisted setting at startup. The same setting also gates the
/// console via a logger rule, so the toggle covers both outputs.
/// </summary>
public static class DatabaseLogSwitch
{
    public const string SettingKey = "log_database_activity";
    public const string EfCommandPrefix = "Microsoft.EntityFrameworkCore.Database.Command";

    public static bool Enabled { get; private set; }
        = Environment.GetEnvironmentVariable("MB_LOG_DB") is "1" or "true";

    public static void Set(bool enabled) => Enabled = enabled;
}
