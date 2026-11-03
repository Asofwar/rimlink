using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Rimlink.Protocol.ModpackDelivery;

public static class ModMetadata
{
    public static DeliveryMod Read(string root, string folderId, int position)
    {
        CheckDirectory(root);
        string about = Path.Combine(root, "About", "About.xml");
        if (!File.Exists(about) || new FileInfo(about).Length > DeliveryLimits.MaxManifestBytes) throw new DeliveryException("InvalidManifest");
        var files = Files(root).ToArray();
        long bytes = 0;
        foreach (string path in files)
        {
            long length = new FileInfo(path).Length;
            if (length > DeliveryLimits.MaxFileBytes || (bytes += length) > DeliveryLimits.MaxExpandedBytes) throw new DeliveryException("Limit");
        }
        using var stream = File.OpenRead(about);
        var inputs = files.Select(path => new ModContentFile(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length, () => File.OpenRead(path))).ToArray();
        return Read(stream, folderId, position, ModContent.ScanFiles(inputs), bytes, files.Length) with { ContentHash = ModContent.FullFilesHash(inputs) };
    }

    internal static DeliveryMod Read(Stream about, string folderId, int position, ModContentInfo content, long bytes, int count)
    {
        try
        {
            if (!DeliveryValidation.Id(folderId) || position < 0) throw new DeliveryException("InvalidManifest");
            using var reader = XmlReader.Create(about, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = DeliveryLimits.MaxManifestBytes });
            var xml = XDocument.Load(reader).Root;
            if (xml?.Name != "ModMetaData") throw new DeliveryException("InvalidManifest");
            string id = DeliveryValidation.Normalize(xml.Element("packageId")?.Value);
            if (DeliveryValidation.Official(id)) throw new DeliveryException("ForbiddenContent");
            string name = xml.Element("name")?.Value.Trim() ?? id, version = xml.Element("modVersion")?.Value.Trim() ?? "";
            if (Encoding.UTF8.GetByteCount(name) > 512 || name.Any(char.IsControl) || version.Length > 64 || version.Any(char.IsControl)) throw new DeliveryException("InvalidManifest");
            string[] supported = Values(xml.Element("supportedVersions"), normalize: false);
            if (!supported.Contains(DeliveryLimits.GameVersion, StringComparer.Ordinal)) throw new DeliveryException("GameVersion");
            var dependencies = Dependencies(xml.Element("modDependencies"))
                .Concat(Dependencies(xml.Element("modDependenciesByVersion")?.Element("v1.7"))).Distinct(StringComparer.Ordinal).ToArray();
            return new DeliveryMod
            {
                PackageId = id, DisplayName = name, Version = version, FolderId = folderId, Position = position,
                SupportedVersions = supported, Dependencies = dependencies,
                Incompatibilities = Values(xml.Element("incompatibleWith")).Concat(Values(xml.Element("incompatibleWithByVersion")?.Element("v1.7"))).Distinct().ToArray(),
                LoadAfter = Values(xml.Element("loadAfter")).Concat(Values(xml.Element("loadAfterByVersion")?.Element("v1.7"))).Distinct().ToArray(),
                LoadBefore = Values(xml.Element("loadBefore")).Concat(Values(xml.Element("loadBeforeByVersion")?.Element("v1.7"))).Distinct().ToArray(),
                FilesHash = content.FilesHash, ExpandedBytes = bytes, FileCount = count,
            };
        }
        catch (XmlException) { throw new DeliveryException("InvalidManifest"); }
    }

    private static string[] Dependencies(XElement? list)
    {
        if (list is null) return [];
        if (list.Elements().Any(e => e.Name != "li" || e.Attributes().Any()
            || e.Elements().Any(n => n.Name.LocalName is not ("packageId" or "displayName" or "steamWorkshopUrl" or "downloadUrl")))) throw new DeliveryException("Dependency");
        return list.Elements("li").Select(e => DeliveryValidation.Normalize(e.Element("packageId")?.Value)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] Values(XElement? node, bool normalize = true)
    {
        if (node is null) return [];
        if (node.Elements().Count() > DeliveryLimits.MaxMods || node.Elements().Any(e => e.Name != "li" || e.HasElements || e.Attributes().Any())) throw new DeliveryException("InvalidManifest");
        return node.Elements("li").Select(e => normalize ? DeliveryValidation.Normalize(e.Value) : e.Value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IEnumerable<string> Files(string root) => Tree(root).Where(path => (File.GetAttributes(path) & FileAttributes.Directory) == 0);

    public static IEnumerable<string> Tree(string root)
    {
        string full = Path.GetFullPath(root);
        int entries = 0;
        var stack = new Stack<string>(); stack.Push(full);
        while (stack.TryPop(out var directory))
        {
            CheckDirectory(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(full, path).Replace('\\', '/');
                if (++entries > DeliveryLimits.MaxEntries || relative.Length > DeliveryLimits.MaxRelativePath || relative.Split('/').Length > DeliveryLimits.MaxDepth) throw new DeliveryException("Limit");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
                if ((attributes & FileAttributes.Directory) != 0) stack.Push(path);
                yield return path;
            }
        }
    }

    public static void CheckDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        for (var at = directory; at is not null; at = at.Parent)
            if (at.Exists && (at.Attributes & FileAttributes.ReparsePoint) != 0) throw new DeliveryException("UnsafePath");
    }
}
