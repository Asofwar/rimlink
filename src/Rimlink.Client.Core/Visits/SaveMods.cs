using System.Xml;

namespace Rimlink.Client.Visits;

/// <summary>
/// The mods a RimWorld save was played with, from its own header (<c>&lt;meta&gt;&lt;modIds&gt;</c> and <c>&lt;modNames&gt;</c>):
/// a host's copy is checked against the visitor's mods before it is loaded, since a mod the visitor lacks would load with the
/// game's errors and its things would silently vanish from the copy. Only the header is read, never the rest of the file.
/// </summary>
public static class SaveMods
{
    /// <summary>The names (or package ids, when the save has no name for one) of the save's mods that <paramref name="isActive"/> rejects, in save order.</summary>
    public static IReadOnlyList<string> Missing(Stream save, Func<string, bool> isActive)
    {
        var ids = new List<string>();
        var names = new List<string>();
        using var reader = XmlReader.Create(save, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        List<string>? into = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "modIds": into = ids; break;
                    case "modNames": into = names; break;
                    case "modSteamIds": into = null; break;
                    case "li" when into is not null: into.Add(reader.ReadElementContentAsString().Trim()); continue;
                    case "game": return Filter(ids, names, isActive); // past the header: nothing more to learn
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "meta")
            {
                return Filter(ids, names, isActive);
            }
        }
        return Filter(ids, names, isActive);
    }

    private static List<string> Filter(List<string> ids, List<string> names, Func<string, bool> isActive)
    {
        var missing = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i].Length == 0 || isActive(ids[i])) continue;
            missing.Add(i < names.Count && names[i].Length > 0 ? names[i] : ids[i]);
        }
        return missing;
    }
}
