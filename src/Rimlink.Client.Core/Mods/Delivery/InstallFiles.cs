using System.Security.Cryptography;
using Rimlink.Protocol.ModpackDelivery;

namespace Rimlink.Client.Mods.Delivery;

internal static class InstallFiles
{
    public static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
    }
    public static void Space(string path, long bytes)
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace < bytes) throw new DeliveryException("DiskSpace");
    }
    public static string Hash(string path)
    {
        NoLinks(path); using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    public static string? ExistingHash(string path) => File.Exists(path) ? Hash(path) : null;
    public static byte[] Read(string path, int max)
    {
        NoLinks(path); using var file = File.OpenRead(path);
        if (file.Length > max) throw new DeliveryException("Limit");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    public static void Write(string path, byte[] bytes)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
