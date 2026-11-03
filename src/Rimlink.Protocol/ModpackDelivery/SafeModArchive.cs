using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rimlink.Protocol.V1;

namespace Rimlink.Protocol.ModpackDelivery;

public sealed record PackedModpack(PackageManifest Manifest, string ArchivePath);

public static class SafeModArchive
{
    private sealed record Input(string Name, string File, bool Directory = false);
    private sealed record Entry(ZipArchiveEntry Zip, string Name, bool Directory);
    private static readonly HashSet<string> ForbiddenFiles = new(StringComparer.OrdinalIgnoreCase)
        { ".env", "auth.json", "Assembly-CSharp.dll", "Waylith.dll", "UnityStubs.dll", "RimWorld.exe", "Player.log", "server.pfx" };
    private static readonly HashSet<string> ForbiddenFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Config", "Saves", "Logs", ".git", ".svn", "tls", "backups" };

    public static PackedModpack PackPreparedCollection(string source, CollectionDescriptor collection, string output, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        ModMetadata.CheckDirectory(source); ModMetadata.CheckDirectory(output);
        if (!Directory.Exists(source) || Directory.EnumerateFiles(source).Any() || Directory.Exists(output) || File.Exists(output)
            || output.StartsWith(source + Path.DirectorySeparatorChar, PathComparison)) throw new DeliveryException("UnsafePath");
        var descriptor = DeliveryValidation.Describe(collection.GameVersion, collection.Entries, []);
        if (descriptor.Revision != collection.Revision || !DeliveryValidation.Sha256(collection.CatalogHash)) throw new DeliveryException("InvalidManifest");
        var wanted = descriptor.Entries.Where(e => e.Kind == EntryKind.Required && !DeliveryValidation.Official(e.PackageId)).ToDictionary(e => e.PackageId);
        var mods = new List<DeliveryMod>(); var inputs = new List<Input>();
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            var mod = ModMetadata.Read(directory, Guid.NewGuid().ToString("N"), 0);
            if (!wanted.TryGetValue(mod.PackageId, out var requirement) || mods.Any(m => m.PackageId == mod.PackageId)) throw new DeliveryException("Incomplete");
            string folder = ModContent.Hash(Encoding.UTF8.GetBytes(mod.PackageId))[..32];
            mod = mod with { FolderId = folder, Position = requirement.Position };
            if (requirement.Version.Length > 0 && mod.Version != requirement.Version || requirement.FilesHash.Length > 0 && mod.FilesHash != requirement.FilesHash) throw new DeliveryException("Incomplete");
            mods.Add(mod);
            foreach (string file in ModMetadata.Tree(directory))
            {
                bool isDirectory = (File.GetAttributes(file) & FileAttributes.Directory) != 0;
                if (isDirectory && Directory.EnumerateFileSystemEntries(file).Any()) continue;
                string relative = "mods/" + folder + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/');
                ValidateRelative(relative); CheckContent(relative); inputs.Add(new Input(relative, file, isDirectory));
            }
        }
        if (mods.Count != wanted.Count || mods.Count == 0 || inputs.Count > DeliveryLimits.MaxEntries) throw new DeliveryException("Incomplete");
        DeliveryValidation.ValidateRelations(mods, descriptor.Entries.Where(e => e.Kind == EntryKind.Required).ToDictionary(e => e.PackageId));
        ValidateNames(inputs.Select(i => (i.Name, i.Directory)));
        Directory.CreateDirectory(output);
        string zip = Path.Combine(output, "archive.zip");
        WriteZip(zip, inputs, [], ct);
        HashSet<string> stored;
        using (var archive = ZipFile.OpenRead(zip))
            stored = archive.Entries.Where(e => e.Length > e.CompressedLength * DeliveryLimits.MaxCompressionRatio).Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        if (stored.Count > 0)
        {
            string replacement = Path.Combine(output, "archive-stored.zip");
            WriteZip(replacement, inputs, stored, ct);
            File.Move(replacement, zip, overwrite: true);
        }
        using var stream = File.OpenRead(zip);
        var manifest = new PackageManifest
        {
            CollectionRevision = descriptor.Revision, CatalogHash = collection.CatalogHash, Entries = descriptor.Entries, GameVersion = descriptor.GameVersion,
            Mods = mods.OrderBy(m => m.Position).ToArray(), Prerequisites = descriptor.Entries.Where(e => e.Kind == EntryKind.Required && DeliveryValidation.Official(e.PackageId)).ToArray(),
            ArchiveBytes = stream.Length, ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(stream)), ExpandedBytes = mods.Sum(m => m.ExpandedBytes), FileCount = mods.Sum(m => m.FileCount),
        };
        ValidateArchive(zip, manifest, ct);
        byte[] json = DeliveryJson.Write(manifest);
        using (var file = new FileStream(Path.Combine(output, "manifest.json"), FileMode.CreateNew, FileAccess.Write)) { file.Write(json); file.Flush(true); }
        return new PackedModpack(manifest, zip);
    }

    public static void ValidateArchive(string archive, PackageManifest manifest, CancellationToken ct = default) => Process(archive, manifest, null, ct);
    public static void ExtractValidated(string archive, PackageManifest manifest, string destination, CancellationToken ct = default) => Process(archive, manifest, Path.GetFullPath(destination), ct);

    private static void Process(string archive, PackageManifest manifest, string? destination, CancellationToken ct)
    {
        DeliveryValidation.Validate(manifest);
        if (destination is not null && (Directory.Exists(destination) || File.Exists(destination))) throw new DeliveryException("UnsafePath");
        ModMetadata.CheckDirectory(Path.GetDirectoryName(Path.GetFullPath(archive))!);
        if ((File.GetAttributes(archive) & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
        using var file = File.OpenRead(archive);
        if (file.Length != manifest.ArchiveBytes || Convert.ToHexStringLower(SHA256.HashData(file)) != manifest.ArchiveSha256) throw new DeliveryException("HashMismatch");
        file.Position = 0;
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is 0 or > DeliveryLimits.MaxEntries) throw new DeliveryException("Limit");
            var entries = new List<Entry>();
            var folders = manifest.Mods.Select(m => m.FolderId).ToHashSet(StringComparer.Ordinal);
            foreach (var item in zip.Entries)
            {
                bool directory = item.FullName.EndsWith('/'); string name = directory ? item.FullName[..^1] : item.FullName;
                ValidateRelative(name); CheckContent(name);
                string[] parts = name.Split('/');
                if (parts.Length < 3 || parts[0] != "mods" || !folders.Contains(parts[1])) throw new DeliveryException("UnsafePath");
                int unixType = (int)((uint)item.ExternalAttributes >> 16) & 0xF000;
                if (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000) || (item.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || !directory && (item.ExternalAttributes & (int)FileAttributes.Directory) != 0) throw new DeliveryException("UnsafePath");
                if (item.Length < 0 || item.Length > DeliveryLimits.MaxFileBytes || item.CompressedLength < 0 || item.CompressedLength > file.Length
                    || item.Length > item.CompressedLength * DeliveryLimits.MaxCompressionRatio || directory && item.Length != 0) throw new DeliveryException("Limit");
                if (destination is not null) _ = GuardedPath(destination, name);
                entries.Add(new Entry(item, name, directory));
            }
            ValidateNames(entries.Select(e => (e.Name, e.Directory)));
            long total = 0, compressed = 0; int count = 0; byte[] buffer = new byte[DeliveryLimits.BufferBytes];
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Directory)
                {
                    if (destination is not null) Directory.CreateDirectory(GuardedPath(destination, entry.Name));
                    continue;
                }
                string? target = destination is null ? null : GuardedPath(destination, entry.Name);
                if (target is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    ModMetadata.CheckDirectory(Path.GetDirectoryName(target)!);
                }
                using var input = entry.Zip.Open();
                using var output = target is null ? null : new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long bytes = 0;
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested(); bytes += read; total += read;
                    if (bytes > entry.Zip.Length || bytes > DeliveryLimits.MaxFileBytes || total > manifest.ExpandedBytes || total > DeliveryLimits.MaxExpandedBytes) throw new DeliveryException("Limit");
                    output?.Write(buffer, 0, read);
                }
                if (bytes != entry.Zip.Length) throw new DeliveryException("InvalidArchive");
                compressed += entry.Zip.CompressedLength; count++;
            }
            if (total != manifest.ExpandedBytes || count != manifest.FileCount || total > compressed * DeliveryLimits.MaxCompressionRatio) throw new DeliveryException("InvalidArchive");
            foreach (var mod in manifest.Mods)
            {
                string prefix = "mods/" + mod.FolderId + "/";
                var modEntries = entries.Where(e => !e.Directory && e.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                var about = modEntries.SingleOrDefault(e => e.Name == prefix + "About/About.xml") ?? throw new DeliveryException("Incomplete");
                if (about.Zip.Length > DeliveryLimits.MaxManifestBytes) throw new DeliveryException("Limit");
                var content = ModContent.ScanFiles(modEntries.Select(e => new ModContentFile(e.Name[prefix.Length..], e.Zip.Length, e.Zip.Open)));
                using var aboutStream = about.Zip.Open();
                var actual = ModMetadata.Read(aboutStream, mod.FolderId, mod.Position, content, modEntries.Sum(e => e.Zip.Length), modEntries.Length) with { ContentHash = ModContent.FullFilesHash(modEntries.Select(e => new ModContentFile(e.Name[prefix.Length..], e.Zip.Length, e.Zip.Open))) };
                if (!DeliveryJson.Write(actual).AsSpan().SequenceEqual(DeliveryJson.Write(mod))) throw new DeliveryException("HashMismatch");
                if (destination is not null && !DeliveryJson.Write(ModMetadata.Read(GuardedPath(destination, "mods/" + mod.FolderId), mod.FolderId, mod.Position)).AsSpan().SequenceEqual(DeliveryJson.Write(mod))) throw new DeliveryException("HashMismatch");
            }
        }
        catch (InvalidDataException) { throw new DeliveryException("InvalidArchive"); }
        catch (NotSupportedException) { throw new DeliveryException("InvalidArchive"); }
    }

    public static string GuardedPath(string root, string relative)
    {
        ValidateRelative(relative);
        root = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison) || path.Length > DeliveryLimits.MaxFullPath) throw new DeliveryException("UnsafePath");
        ModMetadata.CheckDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
        return path;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > DeliveryLimits.MaxRelativePath || path.Contains('\\') || path.Any(char.IsControl)) throw new DeliveryException("UnsafePath");
        string[] parts = path.Split('/');
        if (parts.Length > DeliveryLimits.MaxDepth) throw new DeliveryException("UnsafePath");
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0) throw new DeliveryException("UnsafePath");
            string device = part.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" || device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9' or '¹' or '²' or '³') throw new DeliveryException("UnsafePath");
        }
    }

    private static void CheckContent(string relative)
    {
        string[] parts = relative.Split('/');
        string name = parts[^1];
        if (parts.Skip(2).Any(ForbiddenFolders.Contains) || ForbiddenFiles.Contains(name) || name.EndsWith(".rws", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rws.zst", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(name).ToLowerInvariant() is ".pfx" or ".pem" or ".key") throw new DeliveryException("ForbiddenContent");
    }

    private static void ValidateNames(IEnumerable<(string Name, bool Directory)> entries)
    {
        var all = entries.ToArray(); var names = new HashSet<string>(); var prefixes = new Dictionary<string, string>();
        var files = all.Where(e => !e.Directory).Select(e => Key(e.Name)).ToHashSet();
        foreach (var entry in all)
        {
            if (!names.Add(Key(entry.Name))) throw new DeliveryException("UnsafePath");
            string[] parts = entry.Name.Split('/');
            for (int i = 1; i <= parts.Length; i++)
            {
                string prefix = string.Join('/', parts.Take(i)), key = Key(prefix);
                if (i < parts.Length && files.Contains(key) || prefixes.TryGetValue(key, out var existing) && existing != prefix) throw new DeliveryException("UnsafePath");
                prefixes[key] = prefix;
            }
        }
    }
    private static string Key(string path) => path.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static void WriteZip(string path, List<Input> inputs, HashSet<string> store, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var limited = new LimitedWriteStream(file))
        using (var zip = new ZipArchive(limited, ZipArchiveMode.Create, leaveOpen: true))
        {
            byte[] buffer = new byte[DeliveryLimits.BufferBytes];
            foreach (var input in inputs.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); ModMetadata.CheckDirectory(Path.GetDirectoryName(input.File)!);
                if ((File.GetAttributes(input.File) & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
                if (input.Directory)
                {
                    var directory = zip.CreateEntry(input.Name + "/", CompressionLevel.NoCompression);
                    directory.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); directory.ExternalAttributes = 0x41ED0010;
                    continue;
                }
                var entry = zip.CreateEntry(input.Name, store.Contains(input.Name) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = unchecked((int)0x81A40000);
                using var source = File.OpenRead(input.File); using var destination = entry.Open();
                if (source.Length > DeliveryLimits.MaxFileBytes) throw new DeliveryException("Limit");
                long length = 0; int read;
                while ((read = source.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested(); length += read;
                    if (length > DeliveryLimits.MaxFileBytes) throw new DeliveryException("Limit");
                    destination.Write(buffer, 0, read);
                }
            }
        }
        file.Flush(true);
    }

    private sealed class LimitedWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { if (value > DeliveryLimits.MaxArchiveBytes) throw new DeliveryException("Limit"); inner.SetLength(value); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { if (Position + count > DeliveryLimits.MaxArchiveBytes) throw new DeliveryException("Limit"); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (Position + buffer.Length > DeliveryLimits.MaxArchiveBytes) throw new DeliveryException("Limit"); inner.Write(buffer); }
    }
}
