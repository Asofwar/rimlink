using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Rimlink.Protocol;

/// <summary>What a mod folder holds that matters across games: its def, patch and code files, as one hash plus three flags.</summary>
public sealed record ModContentInfo(string FilesHash, bool HasDefs, bool HasPatches, bool HasAssemblies);

/// <summary>
/// The admin's modpack import and the game compute the same <see cref="ModContentInfo"/> for the same files. Counted are the
/// files under a folder named Defs, Patches or Assemblies at any depth (version folders such as 1.7/ and Common/ included);
/// textures, sounds, translations and About/ are not.
/// </summary>
public static class ModContent
{
    /// <summary>The settings hash of a Mod class that has not written its settings file.</summary>
    public const string NoSettings = "none";

    public const string RimlinkPackageId = "asofwar.rimlink";

    private static readonly string[] Counted = ["defs", "patches", "assemblies"];

    public static ModContentInfo Scan(string modRoot)
    {
        if (!Directory.Exists(modRoot)) throw new DirectoryNotFoundException(modRoot);

        var files = new List<(string Relative, string Path)>();
        bool defs = false, patches = false, assemblies = false;
        foreach (string path in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(modRoot, path).Replace('\\', '/').ToLowerInvariant();
            string[] folders = relative.Split('/')[..^1];
            string? kind = folders.FirstOrDefault(f => Counted.Contains(f));
            if (kind is null) continue;
            defs |= folders.Contains("defs");
            patches |= folders.Contains("patches");
            assemblies |= folders.Contains("assemblies");
            files.Add((relative, path));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (relative, path) in files)
        {
            byte[] content = File.ReadAllBytes(path);
            sha.AppendData(Encoding.UTF8.GetBytes(relative));
            sha.AppendData([0]);
            BitConverter.TryWriteBytes(length, (long)content.Length);
            if (!BitConverter.IsLittleEndian) length.Reverse();
            sha.AppendData(length);
            sha.AppendData(content);
        }
        return new ModContentInfo(Convert.ToHexStringLower(sha.GetHashAndReset()), defs, patches, assemblies);
    }

    /// <summary>Lower-case hex SHA-256.</summary>
    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// The hash of a mod settings file by its content, not its layout: the game rewrites the file (closing a mod's settings
    /// window, another OS's line endings), so the XML is hashed without declaration, BOM and formatting whitespace. A file that
    /// is not XML is hashed as it is.
    /// </summary>
    public static string SettingsHash(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            var root = XDocument.Load(stream, LoadOptions.None).Root;
            if (root is not null) return Hash(Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting)));
        }
        catch (System.Xml.XmlException) { }
        return Hash(content);
    }
}
