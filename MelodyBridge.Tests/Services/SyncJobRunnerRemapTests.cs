using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Extension remap rules (".flac" to ".opus") must rewrite the file
/// extension in the produced M3U. They used to be merged into the path
/// prefix map, where an absolute path can never start with ".flac", so
/// the whole feature was a silent no-op.
/// </summary>
[TestFixture]
public class SyncJobRunnerRemapTests
{
    [Test]
    public async Task ExtensionRemap_RewritesTheExtensionInTheM3u()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mb-ext-remap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "a.flac");
            await File.WriteAllTextAsync(sourcePath, "not really audio; only the path matters");
            var m3uPath = Path.Combine(root, "out.m3u");

            var factory = new TestSqliteFactory(Path.Combine(root, "remap.db"));
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-remap",
                    Name = "Remap",
                    SourceUrl = "stub:remap",
                    TargetDirectory = root,
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "mel-remap", Title = "Song", Artist = "A",
                            DownloadStatus = "downloaded", CurrentPath = sourcePath, Position = 0,
                        },
                    },
                });
                await db.SaveChangesAsync();
            }

            var runner = new SyncJobRunner(factory, new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                Array.Empty<IMediaServerSync>(), NullLogger<SyncJobRunner>.Instance);
            var job = new SyncJob
            {
                Id = "job-remap",
                Name = "Remap",
                SourceId = "pl-remap",
                OutputTarget = OutputTargetType.M3uFile,
                M3uOutputPath = m3uPath,
                ExtensionRemapRules = new Dictionary<string, string> { [".flac"] = ".opus" },
            };

            var log = await runner.RunJobAsync(job);

            Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
            var lines = await File.ReadAllLinesAsync(m3uPath);
            var pathLine = lines.Last();
            Assert.That(pathLine, Does.EndWith(".opus"),
                "the extension rule must apply to the file extension, not as a path prefix");
            Assert.That(pathLine, Does.Not.EndWith(".flac"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
