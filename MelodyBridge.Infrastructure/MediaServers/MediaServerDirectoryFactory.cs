using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Kind-based lookup over the registered <see cref="IMediaServerDirectory"/>
/// instances, with the aliases in one place: "Navidrome" and "Subsonic" are
/// the same API, so a connection stored under either name tests and syncs
/// through the Subsonic directory.
/// </summary>
public sealed class MediaServerDirectoryFactory : IMediaServerDirectoryFactory
{
    /// <summary>Stored kinds that mean another directory's API.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Navidrome"] = "Subsonic",
        ["OpenSubsonic"] = "Subsonic",
        ["Gonic"] = "Subsonic",
        ["Airsonic"] = "Subsonic",
        ["Ampache"] = "Subsonic",
        ["Funkwhale"] = "Subsonic",
    };

    private readonly IReadOnlyList<IMediaServerDirectory> _directories;

    public MediaServerDirectoryFactory(IEnumerable<IMediaServerDirectory> directories)
        => _directories = directories.ToList();

    public IReadOnlyList<IMediaServerDirectory> All => _directories;

    public IMediaServerDirectory? For(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return _directories.Count == 1 ? _directories[0] : null;

        var wanted = kind.Trim();
        if (Aliases.TryGetValue(wanted, out var canonical)) wanted = canonical;

        return _directories.FirstOrDefault(d =>
            string.Equals(d.Kind, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
