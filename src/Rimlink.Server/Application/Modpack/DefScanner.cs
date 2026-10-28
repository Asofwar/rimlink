using System.Xml;

namespace Rimlink.Server.Application.Modpack;

/// <summary>
/// Collects ThingDef and PawnKindDef names from the Defs XML of mod folders without running the game.
/// Abstract defs (<c>Abstract="True"</c>) are bases, never things, and are skipped; unreadable files are ignored.
/// </summary>
public static class DefScanner
{
    private static readonly HashSet<string> DefTypes = ["ThingDef", "PawnKindDef"];

    public static SortedSet<string> Scan(IEnumerable<string> modDirs)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string dir in modDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                if (!file.Replace('\\', '/').Contains("/Defs/", StringComparison.OrdinalIgnoreCase)) continue;
                ScanFile(file, names);
            }
        }
        return names;
    }

    private static void ScanFile(string file, SortedSet<string> names)
    {
        try
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true, IgnoreComments = true });
            string? current = null;
            int defDepth = -1;
            bool advance = true;
            while (!advance || reader.Read())
            {
                advance = true;
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == defDepth) { current = null; defDepth = -1; continue; }
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (current is null && reader.Depth == 1 && DefTypes.Contains(reader.Name))
                {
                    if (string.Equals(reader.GetAttribute("Abstract"), "True", StringComparison.OrdinalIgnoreCase)) { reader.Skip(); advance = false; continue; }
                    current = reader.Name;
                    defDepth = reader.Depth;
                    if (reader.IsEmptyElement) { current = null; defDepth = -1; }
                    continue;
                }
                if (current is not null && reader.Depth == defDepth + 1 && reader.Name == "defName")
                {
                    string name = reader.ReadElementContentAsString().Trim();
                    advance = false;
                    if (name.Length > 0) names.Add(name);
                }
            }
        }
        catch (XmlException)
        {
            // A broken or partial file: nothing to take from it, the rest of the mod still counts.
        }
    }
}
