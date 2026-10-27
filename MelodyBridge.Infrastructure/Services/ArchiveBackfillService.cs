using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Runs one archive backfill at a time and exposes its progress to the
/// Settings page. Creating a new profile does not silently transcode a
/// whole library; the user starts the pass with a button and can see how
/// far it got.
/// </summary>
public sealed class ArchiveBackfillService : IDisposable
{
    private readonly ArchiveCopyService _copies;
    private readonly ILogger<ArchiveBackfillService> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private ArchiveCopyService.BackfillProgress _progress = new(0, 0, 0, null, false, null);

    public ArchiveBackfillService(
        ArchiveCopyService copies, ILogger<ArchiveBackfillService> logger)
    {
        _copies = copies;
        _logger = logger;
    }

    public event Action? Changed;

    public ArchiveCopyService.BackfillProgress Progress
    {
        get { lock (_gate) return _progress; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _task is { IsCompleted: false }; }
    }

    /// <summary>Starts a pass for one profile; a running pass is left alone.</summary>
    public bool Start(string profileId, string profileName)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) return false;
            _cancellation = new CancellationTokenSource();
            _progress = new ArchiveCopyService.BackfillProgress(
                0, 0, 0, null, true, $"Preparing {profileName}…");
            var token = _cancellation.Token;
            _task = Task.Run(async () =>
            {
                var reporter = new Progress<ArchiveCopyService.BackfillProgress>(value =>
                {
                    lock (_gate) _progress = value;
                    Changed?.Invoke();
                });
                try
                {
                    await _copies.BuildMissingAsync(profileId, reporter, token);
                    lock (_gate)
                    {
                        _progress = _progress with { Running = false };
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_gate) _progress = _progress with { Running = false, Message = "Stopped." };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Archive backfill for {Profile} failed", profileName);
                    lock (_gate)
                    {
                        _progress = _progress with
                        {
                            Running = false,
                            Message = "The archive pass stopped: " + ex.Message,
                        };
                    }
                }
                finally
                {
                    Changed?.Invoke();
                }
            }, token);
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    public void Dispose() => _cancellation?.Cancel();
}
