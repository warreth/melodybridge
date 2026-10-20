using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The dashboard connection panel may only claim a server is connected when
/// stored credentials really answer, and it must name the account behind
/// them. A URL on its own (config fallbacks carry localhost defaults) never
/// counts as configured.
/// </summary>
[TestFixture]
public class MediaServerStatusProbeTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;
    private MediaServerProfileStore _profiles = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"melodybridge-probe-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
        _settings = new SettingsStore(_factory);
        _profiles = new MediaServerProfileStore(_settings);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Test]
    public async Task EmptyInstall_ReportsEveryKindAsNotConfigured()
    {
        var directory = new ScriptedDirectory();
        var probe = BuildProbe(directory);

        var snapshot = await probe.SnapshotAsync();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(s => s.Kind),
                Is.EquivalentTo(new[] { "Jellyfin", "Plex", "Subsonic" }));
            Assert.That(snapshot.Select(s => s.State),
                Is.All.EqualTo(MediaServerConnectionState.NotConfigured));
            Assert.That(directory.Tests, Is.Zero,
                "an unconfigured server must not be probed, and must never read as ready");
        });
    }

    [Test]
    public async Task WorkingProfile_ReportsTheServerAndTheAccountThatAnswered()
    {
        await _profiles.SaveAsync(new MediaServerProfile
        {
            Id = "jf-1",
            Name = "Jellyfin Homelab",
            Kind = "Jellyfin",
            BaseUrl = "http://jellyfin:8096",
            ApiKey = "secret-key",
            Username = "mila",
        });
        var directory = new ScriptedDirectory
        {
            Identity = new MediaServerIdentity("Basement", "mila"),
        };
        var probe = BuildProbe(directory);

        var snapshot = await probe.SnapshotAsync();
        var jellyfin = snapshot.Single(s => s.ProfileId == "jf-1");

        Assert.Multiple(() =>
        {
            Assert.That(jellyfin.State, Is.EqualTo(MediaServerConnectionState.Connected));
            Assert.That(jellyfin.Name, Is.EqualTo("Jellyfin Homelab"));
            Assert.That(jellyfin.AccountName, Is.EqualTo("mila"));
            Assert.That(jellyfin.ServerName, Is.EqualTo("Basement"));
            Assert.That(directory.SeenBaseUrl, Is.EqualTo("http://jellyfin:8096"));
            Assert.That(directory.SeenKey, Is.EqualTo("secret-key"));
            Assert.That(directory.SeenUser, Is.EqualTo("mila"));
            Assert.That(snapshot.Count(s => s.Kind == "Jellyfin"), Is.EqualTo(1),
                "a stored profile replaces the legacy row for that kind");
        });
    }

    [Test]
    public async Task ServerThatDoesNotAnswer_IsUnreachableNotReady()
    {
        await _profiles.SaveAsync(new MediaServerProfile
        {
            Id = "plex-1",
            Name = "Living room",
            Kind = "Plex",
            BaseUrl = "http://plex:32400",
            ApiKey = "token",
        });
        var directory = new ScriptedDirectory { Answer = false };
        var probe = BuildProbe(directory);

        var plex = (await probe.SnapshotAsync()).Single(s => s.ProfileId == "plex-1");

        Assert.Multiple(() =>
        {
            Assert.That(plex.State, Is.EqualTo(MediaServerConnectionState.Unreachable));
            Assert.That(plex.Reason, Is.Not.Empty);
            Assert.That(plex.AccountName, Is.Null);
        });
    }

    [Test]
    public async Task SubsonicWithoutAPassword_IsNotConfigured()
    {
        await _profiles.SaveAsync(new MediaServerProfile
        {
            Id = "nav-1",
            Name = "Navidrome",
            Kind = "Navidrome",
            BaseUrl = "http://nav:4533",
            Username = "mila",
            ApiKey = "",
        });
        var directory = new ScriptedDirectory { Kind = "Subsonic" };
        var probe = BuildProbe(directory);

        var subsonic = (await probe.SnapshotAsync()).Single(s => s.ProfileId == "nav-1");

        Assert.That(subsonic.State, Is.EqualTo(MediaServerConnectionState.NotConfigured));
        Assert.That(directory.Tests, Is.Zero);
    }

    [Test]
    public async Task LegacySingleServerValues_AreProbedWhenNoProfileExists()
    {
        var jellyfin = new TestServices.FixedJellyfinSettings
        {
            BaseUrl = "http://old-jellyfin:8096",
            ApiKey = "old-key",
            UserId = "u1",
        };
        var directory = new ScriptedDirectory
        {
            Identity = new MediaServerIdentity("Old server", "mila"),
        };
        var probe = BuildProbe(directory, jellyfin: jellyfin);

        var legacy = (await probe.SnapshotAsync()).Single(s => s.Kind == "Jellyfin");

        Assert.Multiple(() =>
        {
            Assert.That(legacy.ProfileId, Is.EqualTo("legacy:jellyfin"));
            Assert.That(legacy.Name, Is.EqualTo("Jellyfin"));
            Assert.That(legacy.State, Is.EqualTo(MediaServerConnectionState.Connected));
            Assert.That(legacy.AccountName, Is.EqualTo("mila"));
        });
    }

    [Test]
    public async Task Snapshot_IsCachedUntilAnExplicitRefresh()
    {
        await _profiles.SaveAsync(new MediaServerProfile
        {
            Id = "jf-1",
            Name = "Jellyfin",
            Kind = "Jellyfin",
            BaseUrl = "http://jellyfin:8096",
            ApiKey = "key",
        });
        var directory = new ScriptedDirectory();
        var probe = BuildProbe(directory);

        await probe.SnapshotAsync();
        await probe.SnapshotAsync();
        Assert.That(directory.Tests, Is.EqualTo(1), "the dashboard refresh must reuse the cached probe");

        await probe.SnapshotAsync(refresh: true);
        Assert.That(directory.Tests, Is.EqualTo(2));
    }

    private MediaServerStatusProbe BuildProbe(
        ScriptedDirectory directory,
        TestServices.FixedJellyfinSettings? jellyfin = null)
        => new(
            _profiles,
            jellyfin ?? new TestServices.FixedJellyfinSettings(),
            new TestServices.FixedPlexSettings(),
            new TestServices.FixedNavidromeSettings(),
            new MediaServerDirectoryFactory(new IMediaServerDirectory[] { directory }),
            NullLogger<MediaServerStatusProbe>.Instance);

    private sealed class ScriptedDirectory : IMediaServerDirectory
    {
        public string Kind { get; init; } = "Jellyfin";
        public bool Answer { get; init; } = true;
        public MediaServerIdentity? Identity { get; init; }
        public int Tests { get; private set; }
        public string? SeenBaseUrl { get; private set; }
        public string? SeenKey { get; private set; }
        public string? SeenUser { get; private set; }

        public Task<bool> TestConnectionAsync(
            MediaServerConnection connection, CancellationToken ct = default)
        {
            Tests++;
            SeenBaseUrl = connection.BaseUrl;
            SeenKey = connection.ApiKey;
            SeenUser = connection.UserId;
            return Task.FromResult(Answer);
        }

        public Task<MediaServerIdentity?> IdentifyAsync(
            MediaServerConnection connection, CancellationToken ct = default)
            => Task.FromResult(Identity);

        public Task<List<MediaServerUserOption>> GetUsersAsync(
            MediaServerConnection connection, CancellationToken ct = default)
            => Task.FromResult(new List<MediaServerUserOption>());
    }
}
