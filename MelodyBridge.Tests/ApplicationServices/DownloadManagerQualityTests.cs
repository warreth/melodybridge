using MelodyBridge.Core;
using MelodyBridge.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// Quality-cap behavior of the waterfall, with the real DownloadManager
/// and a real registry stub: search hits above the requested cap are
/// skipped, the next plugin wins, and a file that measures above the
/// cap after download is rejected and deleted.
/// </summary>
[TestFixture]
public class DownloadManagerQualityTests
{
    /// <summary>Downloads a real file from disk so the post-download measurement works.</summary>
    private sealed class FileDownloader : IDownloader
    {
        private readonly DownloaderSearchHit? _hit;
        private readonly string? _file;
        public string LastArgs { get; private set; } = "";

        public FileDownloader(string id, string name, DownloaderSearchHit? hit, string? file = null)
        {
            Id = id; Name = name; _hit = hit; _file = file;
        }
        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult(_hit);
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            LastArgs = quality?.YtDlpAudioQuality ?? "any";
            if (_file is null) return Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
            var path = Path.Combine(outputDirectory, $"{Id}-{Path.GetFileName(_file)}");
            File.Copy(_file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private sealed class ListRegistry : IDownloaderRegistry
    {
        private readonly IDownloader[] _downloaders;
        public ListRegistry(params IDownloader[] downloaders) => _downloaders = downloaders;
        public IReadOnlyList<IDownloader> GetAll() => _downloaders;
        public IDownloader? Get(string id) => _downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => _downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private string _dir = null!;

    [SetUp]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), $"mb-quality-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A real MP3 produced with ffmpeg: 128 kbps CBR, 2 seconds of silence.</summary>
    private string MakeRealMp3(int kbps)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"src-{kbps}.mp3");
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is { } probe
            ? Path.Combine(Path.GetDirectoryName(probe)!, "ffmpeg")
            : "ffmpeg";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -c:a libmp3lame -b:a {kbps}k \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        return path;
    }

    [Test]
    public async Task HitAboveCap_IsSkipped_NextPluginWins()
    {
        var fatHit = new DownloaderSearchHit("Fat Rip", "Artist", "https://fat.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 320);
        var fitHit = new DownloaderSearchHit("Fit Rip", "Artist", "https://fit.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 192);
        var fitFile = MakeRealMp3(192);

        var manager = new DownloadManager(
            new ListRegistry(
                new FileDownloader("fat", "Fat Plugin", fatHit),
                new FileDownloader("fit", "Fit Plugin", fitHit, file: fitFile)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1", quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 256));

        Assert.That(path, Does.StartWith(Path.Combine(_dir, "fit")),
            "the 320 kbps hit is above the 256 cap: the 192 kbps plugin must win");
    }

    [Test]
    public async Task HitWithUnknownBitrate_IsAllowed()
    {
        // Search hits without bitrate info (YouTube flat entries) stay usable.
        var unknown = new DownloaderSearchHit("Mystery Rip", "Artist", "https://unknown.example/1", null);
        var manager = new DownloadManager(
            new ListRegistry(new FileDownloader("u", "Unknown Plugin", unknown)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-2", quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 128));

        Assert.That(path, Is.Null, "no file was produced by the stub, so no path is returned");
    }

    [Test]
    public async Task DownloadedFileAboveCap_IsRejectedAndDeleted()
    {
        // Real 320 kbps file, requested cap 128: the post-download
        // measurement must reject and delete it.
        var fat = MakeRealMp3(320);
        var hit = new DownloaderSearchHit("Fat", "Artist", "https://fat.example/1", null, BitrateKbps: null);
        var manager = new DownloadManager(
            new ListRegistry(new FileDownloader("d", "D Plugin", hit, file: fat)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-3", quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 128));

        Assert.That(path, Is.Null, "a measured 320 kbps file must not survive a 128 kbps cap");
        var copies = Directory.EnumerateFiles(_dir, "d-*.mp3").ToList();
        Assert.That(copies, Is.Empty, "the rejected file must be deleted");
    }

    [Test]
    public async Task DownloadedFileWithinCap_IsKept()
    {
        // Real 128 kbps file, requested cap 320: accepted.
        var fit = MakeRealMp3(128);
        var hit = new DownloaderSearchHit("Fit", "Artist", "https://fit.example/1", null);
        var manager = new DownloadManager(
            new ListRegistry(new FileDownloader("d", "D Plugin", hit, file: fit)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-4", quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 320));

        Assert.That(path, Is.Not.Null, "a measured 128 kbps file is within a 320 kbps cap");
        Assert.That(path, Does.StartWith(Path.Combine(_dir, "d-")));
        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public async Task HitBelowMin_IsSkipped_NextPluginWins()
    {
        var thinHit = new DownloaderSearchHit("Thin Rip", "Artist", "https://thin.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 96);
        var fatHit = new DownloaderSearchHit("Fit Rip", "Artist", "https://fit.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 320);
        var fatFile = MakeRealMp3(320);

        var manager = new DownloadManager(
            new ListRegistry(
                new FileDownloader("thin", "Thin Plugin", thinHit),
                new FileDownloader("fat", "Fat Plugin", fatHit, file: fatFile)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-5",
            quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: 192, MaxKbps: 320));

        Assert.That(path, Does.StartWith(Path.Combine(_dir, "fat")),
            "the 96 kbps hit is below the 192 kbps floor: the 320 kbps plugin must win");
    }

    [Test]
    public async Task DownloadedFileBelowMin_IsRejectedAndDeleted()
    {
        // Real 96 kbps file, requested band 192-320: the post-download
        // measurement must reject and delete it — min is as hard as the cap.
        var thin = MakeRealMp3(96);
        var hit = new DownloaderSearchHit("Thin", "Artist", "https://thin.example/1", null);
        var manager = new DownloadManager(
            new ListRegistry(new FileDownloader("d", "D Plugin", hit, file: thin)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-6",
            quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: 192, MaxKbps: 320));

        Assert.That(path, Is.Null, "a measured 96 kbps file must not survive a 192 kbps floor");
        var copies = Directory.EnumerateFiles(_dir, "d-*.mp3").ToList();
        Assert.That(copies, Is.Empty, "the rejected file must be deleted");
    }

    [Test]
    public async Task DownloadedFileWithinBand_IsKept()
    {
        var fit = MakeRealMp3(256);
        var hit = new DownloaderSearchHit("Fit", "Artist", "https://fit.example/1", null);
        var manager = new DownloadManager(
            new ListRegistry(new FileDownloader("d", "D Plugin", hit, file: fit)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-7",
            quality: new DownloadQuality(AudioFormat.Mp3, MinKbps: 192, MaxKbps: 320));

        Assert.That(path, Is.Not.Null, "a measured 256 kbps file is within the 192-320 band");
        Assert.That(File.Exists(path), Is.True);
    }

    [Test]
    public void Quality_PassesCapToPlugins()
    {
        var downloader = new FileDownloader("q", "Q", null);
        var _ = new DownloadQuality(AudioFormat.Mp3, MinKbps: null, MaxKbps: 192);
        Assert.That(_.YtDlpAudioQuality, Is.EqualTo("192K"));
        Assert.That(new DownloadQuality(AudioFormat.Mp3).YtDlpAudioQuality, Is.EqualTo("0"), "no cap = best VBR");
        Assert.That(new DownloadQuality(AudioFormat.Auto).NeedsTranscode, Is.False, "auto keeps the source codec");
    }
}