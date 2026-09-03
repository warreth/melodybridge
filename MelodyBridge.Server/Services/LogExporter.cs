using System.Text;
using MelodyBridge.Core.Logging;

namespace MelodyBridge.Server.Services;

/// <summary>
/// Exports collected logs to text formats for download or debugging.
/// </summary>
public class LogExporter
{
    private readonly ILogCollector _collector;

    public LogExporter(ILogCollector collector)
    {
        _collector = collector;
    }

    /// <summary>
    /// Export all current log entries as a human-readable text file.
    /// </summary>
    public string ExportToText()
    {
        var entries = _collector.GetEntries();
        var sb = new StringBuilder();
        sb.AppendLine("───────────────────────────────────────────────────────────────");
        sb.AppendLine("  MelodyBridge: Log Export");
        sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  Entries:   {entries.Count}");
        sb.AppendLine("───────────────────────────────────────────────────────────────");
        sb.AppendLine();

        foreach (var entry in entries.Reverse()) // oldest first for chronological reading
        {
            var timestamp = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
            var level = entry.Level.ToString().ToUpperInvariant().PadRight(8);
            sb.AppendLine($"[{timestamp}] {level} [{entry.Category}] {entry.Message}");
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                // Indent multi-line details
                foreach (var line in entry.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine($"         {line.Trim()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("───────────────────────────────────────────────────────────────");
        sb.AppendLine("  End of export");
        sb.AppendLine("───────────────────────────────────────────────────────────────");

        return sb.ToString();
    }

    /// <summary>
    /// Export as a byte array (UTF-8) suitable for file download.
    /// </summary>
    public byte[] ExportToBytes()
    {
        return Encoding.UTF8.GetBytes(ExportToText());
    }
}
