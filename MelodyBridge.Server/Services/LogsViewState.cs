namespace MelodyBridge.Server.Services;

/// <summary>
/// The Logs page filter that survives leaving the page and coming back.
/// Kept in memory for the running server; a restart starts clean.
/// </summary>
public sealed class LogsViewState
{
    public string? SelectedArea { get; set; }
    public string? SelectedLevel { get; set; }
    public string SelectedSource { get; set; } = "all";
    public string SearchText { get; set; } = string.Empty;
    public bool NewestFirst { get; set; } = true;
    public bool GroupByTrack { get; set; }
}
