using System.Runtime.InteropServices;
using MelodyBridge.Infrastructure.Files;

namespace MelodyBridge.Tests.Files;

/// <summary>
/// The struct stat parser per ABI. The old code read offset 16 on every
/// Unix: that is st_uid on macOS and st_mode on arm64 Linux, so the link
/// count was a user id or a file mode there and the dedup stale-link
/// cleanup silently misbehaved. Synthetic buffers pin the three layouts;
/// the real link-count tests cover the host's layout.
/// </summary>
[TestFixture]
public class UnixStatLayoutTests
{
    private static byte[] Buffer(int length = 32)
    {
        var buffer = new byte[length];
        // Poison, so a wrong field cannot accidentally read zero.
        Array.Fill(buffer, (byte)0xAB);
        return buffer;
    }

    [Test]
    public void LinuxX64_ReadsNlinkFromOffset16()
    {
        var buffer = Buffer();
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), 0x1122334455667788L);  // st_dev
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), 0xCAFEBABE12345678UL); // st_ino
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 8), 3UL);                 // st_nlink

        var stat = HardLinkService.ParseUnixStat(buffer, HardLinkService.UnixStatLayout.LinuxX64);

        Assert.Multiple(() =>
        {
            Assert.That(stat.LinkCount, Is.EqualTo(3UL));
            Assert.That(stat.Inode, Is.EqualTo(0xCAFEBABE12345678UL));
        });
    }

    [Test]
    public void MacOS_ReadsNlinkFromOffset6()
    {
        var buffer = Buffer();
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 0x11223344);           // st_dev
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)0x81A4);       // st_mode
        BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), (ushort)2);            // st_nlink
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), 0xFEEDFACE12345678UL); // st_ino
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), 501);                 // st_uid (the old bug read this)

        var stat = HardLinkService.ParseUnixStat(buffer, HardLinkService.UnixStatLayout.MacOS);

        Assert.Multiple(() =>
        {
            Assert.That(stat.LinkCount, Is.EqualTo(2UL),
                "macOS st_nlink lives at offset 6, not 16");
            Assert.That(stat.Inode, Is.EqualTo(0xFEEDFACE12345678UL));
        });
    }

    [Test]
    public void LinuxArm64_ReadsNlinkFromOffset20()
    {
        var buffer = Buffer();
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), 0x1122334455667788L);  // st_dev
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), 0xCAFEBABE87654321UL); // st_ino
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), 0x81A4u);             // st_mode
        BitConverter.TryWriteBytes(buffer.AsSpan(20, 4), 5u);                  // st_nlink

        var stat = HardLinkService.ParseUnixStat(buffer, HardLinkService.UnixStatLayout.LinuxArm64);

        Assert.Multiple(() =>
        {
            Assert.That(stat.LinkCount, Is.EqualTo(5UL),
                "arm64 st_nlink lives at offset 20, not 16");
            Assert.That(stat.Inode, Is.EqualTo(0xCAFEBABE87654321UL));
        });
    }

    [Test]
    public void CurrentLayout_MatchesTheRunningPlatform()
    {
        var expected = OperatingSystem.IsMacOS()
            ? HardLinkService.UnixStatLayout.MacOS
            : RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? HardLinkService.UnixStatLayout.LinuxArm64
                : HardLinkService.UnixStatLayout.LinuxX64;

        Assert.That(HardLinkService.CurrentLayout, Is.EqualTo(expected));
    }
}
