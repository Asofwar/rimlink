namespace Rimlink.Server.Tests.Support;

internal static class LocalTestStorage
{
    // Fixtures explicitly qualify their real local mount; this never changes production defaults or accepts an unsupported mount.
    internal static string LinuxQualification(string path)
    {
        if (!OperatingSystem.IsLinux()) return "";
        string full = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Where(d => {
            string mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d.Name));
            return full == mount || full.StartsWith(Path.EndsInDirectorySeparator(mount) ? mount : mount + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }).OrderByDescending(d => d.Name.Length).First().DriveFormat;
    }
}
