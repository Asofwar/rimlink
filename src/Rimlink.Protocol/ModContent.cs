using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Rimlink.Protocol;

/// <summary>What a mod folder holds that matters across games: its def, patch and code files, as one hash plus three flags.</summary>
public sealed record ModContentInfo(string FilesHash, bool HasDefs, bool HasPatches, bool HasAssemblies);
public sealed record ModContentFile(string Relative, long Length, Func<Stream> Open);

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
        return ScanFiles(Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories).Select(path =>
            new ModContentFile(Path.GetRelativePath(modRoot, path).Replace('\\', '/'), new FileInfo(path).Length, () => File.OpenRead(path))));
    }

    /// <summary>The same wire hash from disk or validated ZIP entries, without extracting the archive.</summary>
    public static ModContentInfo ScanFiles(IEnumerable<ModContentFile> inputs)
    {
        var files = new List<ModContentFile>();
        bool defs = false, patches = false, assemblies = false;
        foreach (var input in inputs)
        {
            string relative = input.Relative.Replace('\\', '/').ToLowerInvariant();
            string[] folders = relative.Split('/')[..^1];
            string? kind = folders.FirstOrDefault(f => Counted.Contains(f));
            if (kind is null) continue;
            defs |= folders.Contains("defs");
            patches |= folders.Contains("patches");
            assemblies |= folders.Contains("assemblies");
            files.Add(input with { Relative = relative });
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        byte[] buffer = files.Count == 0 ? [] : new byte[64 * 1024];
        foreach (var file in files)
        {
            using var stream = file.Open();
            long size = file.Length;
            if (size < 0) throw new IOException("invalid mod file length");
            sha.AppendData(Encoding.UTF8.GetBytes(file.Relative));
            sha.AppendData([0]);
            BitConverter.TryWriteBytes(length, size);
            if (!BitConverter.IsLittleEndian) length.Reverse();
            sha.AppendData(length);
            long read = 0;
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                read += count;
                if (read > size) throw new IOException("mod file changed during hashing");
                sha.AppendData(buffer.AsSpan(0, count));
            }
            if (read != size || stream.CanSeek && stream.Length != size) throw new IOException("mod file changed during hashing");
        }
        return new ModContentInfo(Convert.ToHexStringLower(sha.GetHashAndReset()), defs, patches, assemblies);
    }

    /// <summary>Lower-case hex SHA-256.</summary>
    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>All file names and bytes, including cosmetics and About: exact reuse is stricter than the gameplay wire hash.</summary>
    public static string FullFilesHash(IEnumerable<ModContentFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var file in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative)); hash.AppendData([0]);
            using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var stream = file.Open();
            long length = 0; int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                length += read; if (length > file.Length) throw new IOException("mod file changed during hashing");
                content.AppendData(buffer.AsSpan(0, read));
            }
            if (length != file.Length) throw new IOException("mod file changed during hashing");
            hash.AppendData(content.GetHashAndReset());
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

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
