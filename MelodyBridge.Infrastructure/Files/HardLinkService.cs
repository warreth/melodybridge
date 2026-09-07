using System.Runtime.InteropServices;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// Native OS hard links through one interface. There is no master pool:
/// every hard link points at a normal file inside some playlist's folder,
/// so the folder structure stays exactly what the user chose.
///
/// The managed File.CreateHardLink API only ships with .NET 11; this app
/// targets net8.0, so the same call goes to the OS entry points directly:
/// link(2) on Unix (errno EXDEV when the paths sit on different
/// filesystems) and CreateHardLinkW on Windows (ERROR_NOT_SAME_DEVICE).
/// ponytail: swap both branches for File.CreateHardLink once the app
/// moves to .NET 11 or later.
/// </summary>
public sealed class HardLinkService : IHardLinkService
{
    public bool IsSupported
        => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public void Create(string linkPath, string targetPath)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("hard links need Windows, Linux or macOS");

        // Hard links cannot cross filesystems. Fail fast with the honest
        // error instead of a cryptic OS code, so the caller can fall back.
        if (!SameFileSystem(
                Path.GetDirectoryName(Path.GetFullPath(linkPath)) ?? linkPath,
                Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? targetPath))
            throw new IOException(
                $"cross-device link: '{linkPath}' and '{targetPath}' are on different filesystems");

        if (OperatingSystem.IsWindows())
            CreateWindows(linkPath, targetPath);
        else
            CreateUnix(linkPath, targetPath);
    }

    private static void CreateUnix(string linkPath, string targetPath)
    {
        // link(2): existing path first, new link name second.
        if (link(targetPath, linkPath) != 0)
        {
            var errno = Marshal.GetLastWin32Error();
            throw new IOException(
                $"hard link failed (errno {errno}): {Strerror(errno)}", errno);
        }
    }

    private static void CreateWindows(string linkPath, string targetPath)
    {
        if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CreateHardLink failed (win32 {error})", error);
        }
    }

    /// <summary>
    /// True when both paths sit on the same filesystem. Unix compares the
    /// device id from stat(2); Windows compares drive letters, which is
    /// the practical hard link boundary there.
    /// </summary>
    public bool SameFileSystem(string pathA, string pathB)
    {
        if (!IsSupported) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                static char Drive(string p)
                {
                    var root = Path.GetPathRoot(Path.GetFullPath(p));
                    return char.ToUpperInvariant(root?.TrimEnd('\\', '/').FirstOrDefault() ?? '\\');
                }
                return Drive(pathA) == Drive(pathB);
            }

            return Stat(pathA, out var a) == 0 && Stat(pathB, out var b) == 0
                && a.Device == b.Device;
        }
        catch
        {
            // A path that cannot be stat'ed (missing folder) cannot be
            // judged either way: report false so callers fall back.
            return false;
        }
    }

    /// <summary>Hard link count of the file, null when it cannot be read.</summary>
    public int? LinkCount(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) return null;
                if (!GetFileInformationByHandle(handle, out var info)) return null;
                return (int)info.nNumberOfLinks;
            }

            return Stat(path, out var s) == 0 && s.LinkCount > 0 ? (int)s.LinkCount : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Unix interop ----

    // The full glibc/macOS struct stat must be declared, not a prefix:
    // stat(2) writes the whole struct, and a short buffer would corrupt
    // the memory behind it. Both layouts are 144 bytes on x64/arm64 but
    // their field widths differ, so each OS gets its own shape.
    private static unsafe int Stat(string path, out UnixStat st)
    {
        // The fields this class needs live in the first 24 bytes on both
        // layouts; the rest is padding the kernel also writes.
        var buf = stackalloc byte[256];
        var r = stat(path, buf);
        st = OperatingSystem.IsMacOS()
            ? new UnixStat(*(int*)buf, *(ulong*)(buf + 8), *(ushort*)(buf + 16))
            : new UnixStat(*(long*)buf, *(ulong*)(buf + 8), *(ulong*)(buf + 16));
        return r;
    }

    private readonly record struct UnixStat(long Device, ulong Inode, ulong LinkCount);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int link(string oldpath, string newpath);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int stat(string path, void* buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr strerror(int errnum);

    private static string Strerror(int errno)
    {
        var ptr = strerror(errno);
        return ptr == IntPtr.Zero ? $"errno {errno}" : Marshal.PtrToStringAnsi(ptr) ?? $"errno {errno}";
    }

    // ---- Windows interop ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint dwFileIndexHigh;
        public uint dwFileIndexLow;
    }
}
