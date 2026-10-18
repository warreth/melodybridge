using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Kind-based lookup over the registered <see cref="IMediaServerDirectory"/>
/// instances; the aliases live in <see cref="MediaServerKinds"/>, so
/// "Navidrome" and "Subsonic" resolve to the same directory.
/// </summary>
public sealed class MediaServerDirectoryFactory : IMediaServerDirectoryFactory
{
    private readonly IReadOnlyList<IMediaServerDirectory> _directories;

    public MediaServerDirectoryFactory(IEnumerable<IMediaServerDirectory> directories)
        => _directories = directories.ToList();

    public IReadOnlyList<IMediaServerDirectory> All => _directories;

    public IMediaServerDirectory? For(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return _directories.Count == 1 ? _directories[0] : null;

        var wanted = MediaServerKinds.Canonical(kind);
        return _directories.FirstOrDefault(d =>
            string.Equals(d.Kind, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
