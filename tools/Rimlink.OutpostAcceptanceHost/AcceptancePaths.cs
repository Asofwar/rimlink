using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rimlink.Server.Application.Outposts;

namespace Rimlink.OutpostAcceptanceHost;

internal static class AcceptancePaths
{
    internal static string Directory(string path) => OutpostStorageProfile.CanonicalExistingDirectory(path);
    internal static bool Within(string path, string root) => path.Equals(root, Comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static void File(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !System.IO.File.Exists(path) || (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("acceptance-file-unavailable-or-aliased");
        _ = Directory(Path.GetDirectoryName(path)!);
        var (_, links, directory) = Identity(path);
        if (links != 1 || directory) throw new InvalidDataException("acceptance-file-hard-link-or-type");
    }
    internal static string DirectoryIdentity(string path)
    {
        var (identity, _, directory) = Identity(path);
        return directory ? identity : throw new InvalidDataException("acceptance-directory-identity-unavailable");
    }

    private static (string Id, uint Links, bool Directory) Identity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new InvalidDataException("acceptance-file-identity-unavailable");
            return ($"{info.Volume:x8}:{info.IndexHigh:x8}:{info.IndexLow:x8}", info.Links, (info.Attributes & 0x10) != 0);
        }
        if (OperatingSystem.IsLinux())
        {
            // Linux UAPI statx layout; inspect type/inode/link-count, never follow the final component.
            if (statx(-100, path, 0x100, 0x7ff, out var info) != 0 || (info.Mask & 0x105) != 0x105)
                throw new InvalidDataException("acceptance-file-identity-unavailable");
            return ($"{info.DeviceMajor:x8}:{info.DeviceMinor:x8}:{info.Inode:x16}", info.Links, (info.Mode & 0xf000) == 0x4000);
        }
        throw new InvalidDataException("acceptance-platform-unsupported");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsInfo
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxInfo
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsInfo info);
    [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int statx(int directory, string path, int flags, uint mask, out LinuxInfo info);
}
