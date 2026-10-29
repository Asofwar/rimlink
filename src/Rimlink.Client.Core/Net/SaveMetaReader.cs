using System.Globalization;
using System.Xml;

namespace Rimlink.Client.Net;

/// <summary>
/// The upload header of a .rws on disk, for a save uploaded later than it was written (the pending upload at login): seed,
/// player faction and tick from the game's own XML, wealth and the dev-mode flag from what the Rimlink game component wrote
/// into the save. A save written before those values existed reads as 0 wealth and no flag.
/// </summary>
public static class SaveMetaReader
{
    public const string DevModeNode = "rimlinkDevModeSinceUpload";
    public const string WealthTotalNode = "rimlinkWealthTotal";
    public const string WealthItemsNode = "rimlinkWealthItems";

    public static SaveMeta Read(string rwsPath)
    {
        string seed = "", faction = "";
        long tick = 0, wealthTotal = 0, wealthItems = 0;
        bool devMode = false;
        using var reader = XmlReader.Create(rwsPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        string? pendingDef = null;
        // ReadElementContentAsString leaves the reader on the next node, so do not Read() again in that case.
        bool advance = true;
        while (!advance || reader.Read())
        {
            advance = true;
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "seedString" when seed.Length == 0: seed = reader.ReadElementContentAsString(); advance = false; break;
                case "ticksGame" when tick == 0: tick = long.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture); advance = false; break;
                case "def" when faction.Length == 0: pendingDef = reader.ReadElementContentAsString(); advance = false; break;
                case "name" when faction.Length == 0 && pendingDef is "PlayerColony" or "PlayerTribe": faction = reader.ReadElementContentAsString(); advance = false; break;
                case DevModeNode: devMode = bool.Parse(reader.ReadElementContentAsString()); advance = false; break;
                case WealthTotalNode: wealthTotal = long.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture); advance = false; break;
                case WealthItemsNode: wealthItems = long.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture); advance = false; break;
            }
        }
        return new SaveMeta(seed, faction, tick, wealthTotal, wealthItems, devMode);
    }
}
