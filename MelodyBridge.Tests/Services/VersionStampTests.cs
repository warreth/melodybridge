using System.Reflection;
using MelodyBridge.Core;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The version the app reports comes from the assembly (stamped by the
/// release workflow) with the constant only as a fallback, and the release
/// plumbing really passes the tag through: the old hardcoded const could
/// silently disagree with every published image.
/// </summary>
[TestFixture]
public class VersionStampTests
{
    [Test]
    public void Version_ComesFromTheAssemblyInformationalVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var expected = string.IsNullOrWhiteSpace(informational)
            ? AppInfo.FallbackVersion
            : informational.Split('+')[0];

        Assert.That(AppInfo.Version, Is.EqualTo(expected));
    }

    [Test]
    public void ReleasePlumbing_StampsTheVersionIntoTheImage()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "docker.yml"));

        Assert.Multiple(() =>
        {
            Assert.That(dockerfile, Does.Contain("ARG VERSION"));
            Assert.That(dockerfile, Does.Contain("-p:Version=$VERSION"),
                "the image build must stamp the version into the assembly");
            Assert.That(workflow, Does.Contain("VERSION="),
                "the release workflow must pass the tag as the build arg");
        });
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFileSystemInfos(".git").Any())
            dir = dir.Parent!;
        return dir!.FullName;
    }
}
