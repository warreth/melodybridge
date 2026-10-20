namespace MelodyBridge.Core.Logging;

/// <summary>
/// The track or run a piece of work belongs to, carried through async
/// calls without passing a logger around. The Dev panel provider stamps
/// every entry with it, so the Logs page can keep one attempt's search,
/// skip reasons, download and verification together.
/// </summary>
public static class LogCorrelation
{
    private static readonly AsyncLocal<string?> CurrentGroup = new();

    public static string? Current => CurrentGroup.Value;

    /// <summary>Starts a group; dispose restores the previous one.</summary>
    public static IDisposable Begin(string? group) => new Scope(CurrentGroup.Value, group);

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string? previous, string? next)
        {
            _previous = previous;
            CurrentGroup.Value = next;
        }

        public void Dispose() => CurrentGroup.Value = _previous;
    }
}
