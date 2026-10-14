namespace MelodyBridge.Core;

/// <summary>
/// Receives serious background failures that would otherwise only reach
/// the log: a failed auto-sync refresh, a failed scheduled scan, a crashed
/// download run. The notification layer decides what to do with them
/// (toast, ntfy, or nothing) from the user's settings.
/// </summary>
public interface IBackgroundFailureSink
{
    /// <summary>Reports one failure. Never throws, so a catch block can await it.</summary>
    Task ReportAsync(string message);
}
