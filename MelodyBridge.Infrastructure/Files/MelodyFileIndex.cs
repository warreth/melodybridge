using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// One read of a playlist folder for adoption and duplicate checks: a
/// MELODY_ID index plus the files whose id is old or missing. The metadata
/// of the fallback files is read lazily, so a folder whose ids all match
/// costs one tag read per file and no title/duration probes.
/// </summary>
internal sealed class MelodyFileIndex
{
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".webm",
    };

    private readonly Dictionary<string, List<string>> _byId;
    private readonly List<CandidateFile> _fallback;
    private readonly Dictionary<string, AudioFileIdentity> _identities = new(StringComparer.Ordinal);

    private MelodyFileIndex(
        Dictionary<string, List<string>> byId, List<CandidateFile> fallback)
    {
        _byId = byId;
        _fallback = fallback;
    }

    /// <summary>All audio files the index saw, with their stored id when one exists.</summary>
    public IReadOnlyList<CandidateFile> Files { get; private set; } = Array.Empty<CandidateFile>();

    /// <summary>
    /// Scans <paramref name="root"/> recursively. Files under an archive
    /// root never enter the index. <paramref name="directIds"/> are the ids
    /// the caller can use as-is; files carrying them stay out of the
    /// metadata fallback list.
    /// </summary>
    public static MelodyFileIndex Build(
        string root, IReadOnlyCollection<string> archiveRoots, IReadOnlySet<string>? directIds = null)
    {
        var byId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var fallback = new List<CandidateFile>();
        var files = new List<CandidateFile>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new MelodyFileIndex(byId, fallback) { Files = files };

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!AudioExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                if (archiveRoots.Any(archive => FileRelocator.IsUnder(file, archive)))
                    continue;

                var id = TaglibHelper.ReadMelodyId(file);
                files.Add(new CandidateFile(file, id));
                if (!string.IsNullOrWhiteSpace(id))
                {
                    if (!byId.TryGetValue(id, out var paths))
                        byId[id] = paths = new List<string>();
                    paths.Add(file);
                    if (directIds is not null && directIds.Contains(id)) continue;
                }
                fallback.Add(new CandidateFile(file, id));
            }
        }
        catch (Exception)
        {
            // An unreadable folder leaves the index partial; adoption simply
            // finds less and the normal download path remains available.
        }

        return new MelodyFileIndex(byId, fallback) { Files = files };
    }

    /// <summary>
    /// The best file carrying the exact id. A plain name wins over a
    /// numbered duplicate so the original becomes the adopted copy.
    /// </summary>
    public string? Find(string melodyId)
        => _byId.TryGetValue(melodyId, out var paths)
            ? PickPreferred(paths)
            : null;

    /// <summary>
    /// A file with an old or missing id whose tags match the track. Only a
    /// High-confidence, duration-plausible and unique match is accepted;
    /// ambiguity is never guessed. Files owned by another row or path are
    /// skipped through the two predicates.
    /// </summary>
    public string? FindByMetadata(
        string? artist, string? title, TimeSpan? duration,
        Func<string?, bool> isIdOwnedByAnotherRow,
        Func<string, bool> isPathOwnedByAnotherRow)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string? found = null;
        foreach (var file in _fallback)
        {
            if (file.MelodyId is { Length: > 0 } id && isIdOwnedByAnotherRow(id)) continue;
            if (isPathOwnedByAnotherRow(file.Path)) continue;

            var identity = Identity(file.Path);
            if (string.IsNullOrWhiteSpace(identity.Title) || string.IsNullOrWhiteSpace(identity.Artist))
                continue;
            if (FuzzyMatcher.Confidence(artist, title, identity.Artist, identity.Title)
                != MatchConfidence.High)
                continue;
            if (!FuzzyMatcher.DurationPlausible(duration, identity.Duration)) continue;

            if (found is not null) return null; // more than one candidate: do not guess
            found = file.Path;
        }
        return found;
    }

    /// <summary>The tag facts of one indexed file, cached for one pass.</summary>
    public AudioFileIdentity Identity(string path)
    {
        if (_identities.TryGetValue(path, out var identity)) return identity;
        identity = TaglibHelper.ReadIdentity(path);
        _identities[path] = identity;
        return identity;
    }

    private static string PickPreferred(List<string> paths)
        => paths
            .OrderBy(path => Path.GetFileName(path).Contains(" (", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(path => path.Length)
            .First();

    internal sealed record CandidateFile(string Path, string? MelodyId);
}
