namespace MelodyBridge.Application.Services;

/// <summary>
/// One line for the sidebar activity chip: how many runs are active, how
/// far they are and how many tracks failed. Pure so the number formatting
/// is testable without a running coordinator.
/// </summary>
public sealed record DownloadActivitySummary(
    int Runs,
    int Paused,
    int Total,
    int Done,
    int Failed)
{
    public int Percent => Total == 0 ? 0 : Math.Min(100, (Done + Failed) * 100 / Total);

    public string Headline => Runs == 0
        ? "No active downloads"
        : Paused == Runs
            ? (Runs == 1 ? "1 playlist paused" : $"{Runs} playlists paused")
            : (Runs == 1 ? "1 playlist downloading" : $"{Runs} playlists downloading");

    public string Detail => Failed > 0
        ? $"{Done}/{Total} done · {Failed} failed"
        : $"{Done}/{Total} done";

    public static DownloadActivitySummary From(IReadOnlyList<DownloadRun> runs)
    {
        var active = runs
            .Where(run => run.State is DownloadRunState.Running
                or DownloadRunState.Paused or DownloadRunState.Cancelling)
            .ToList();

        return new DownloadActivitySummary(
            active.Count,
            active.Count(run => run.State == DownloadRunState.Paused),
            active.Sum(run => run.Total),
            active.Sum(run => run.Done),
            active.Sum(run => run.Failed));
    }
}
