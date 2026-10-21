using MelodyBridge.Core;

namespace MelodyBridge.Server.Components.Shared;

/// <summary>
/// Everything one open source picker needs. The picker owns the dialog,
/// the streaming result list, the filters and the probes; the caller owns
/// where the search runs and what a pick does, so the playlist page and
/// the manual download page share the whole interaction.
/// </summary>
/// <param name="Key">
/// Identifies this dialog. Opening the same key again keeps the state;
/// a different key starts a fresh search. Used for the scroll memory too.
/// </param>
/// <param name="SearchAsync">
/// Streams candidates through the callback as each plugin answers, then
/// returns the final list and the plugins that could not be searched.
/// </param>
/// <param name="ProbeAsync">Reads one candidate's exact audio facts, best effort.</param>
/// <param name="DownloadAsync">
/// Performs the picked download. Downloaded and AlreadyRunning close the
/// dialog; Failed keeps it open and shows the reason on the row.
/// </param>
/// <param name="Intro">
/// One line under the title. Playlist picks explain that the automatic
/// match refused the results; a manual search did not have a match to
/// refuse, so it passes its own copy.
/// </param>
public sealed record SourcePickerRequest(
    string Key,
    string Title,
    string? Artist,
    long? DurationMs,
    DownloadQuality TargetQuality,
    string TargetQualityLabel,
    Func<Action<DownloadCandidate>, CancellationToken, Task<SourceSearchOutcome>> SearchAsync,
    Func<DownloadCandidate, CancellationToken, Task<CandidateAudioQuality?>> ProbeAsync,
    Func<DownloadCandidate, Task<SourcePickResult>> DownloadAsync,
    string? Intro = null);

public enum SourcePickStatus
{
    Downloaded,
    AlreadyRunning,
    Failed,
}

/// <summary>
/// Outcome of one pick. The message is the success toast; Failure is the
/// row-level reason a pick did not deliver.
/// </summary>
public sealed record SourcePickResult(
    SourcePickStatus Status,
    string? Failure = null,
    string? Message = null)
{
    public static SourcePickResult Success(string? message = null)
        => new(SourcePickStatus.Downloaded, Message: message);

    public static SourcePickResult AlreadyRunning { get; }
        = new(SourcePickStatus.AlreadyRunning);

    public static SourcePickResult Failed(string? failure)
        => new(SourcePickStatus.Failed, Failure: failure);
}
