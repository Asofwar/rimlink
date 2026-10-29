using Rimlink.Protocol;
using Rimlink.Server.Grpc;
using System.Xml;
using ZstdSharp;

namespace Rimlink.Server.Application.Saves;

/// <summary>
/// Checks that a zstd-compressed upload is a RimWorld save for this world with a player faction in it:
/// root &lt;savegame&gt;, the first &lt;seedString&gt; equals the world seed, and &lt;allFactions&gt; holds a faction whose &lt;def&gt;
/// is the scenario's player faction def (&lt;playerFaction&gt;&lt;factionDef&gt;), or PlayerColony / PlayerTribe.
/// The faction's &lt;name&gt; is optional (RimWorld writes none until the player names the faction) and is returned when present.
/// Reads at most <see cref="Limits.MaxSaveBytes"/> of decompressed data. Returns null when valid, otherwise the reason.
/// </summary>
public static class SaveValidator
{
    public static SaveRejected? Validate(string zstPath, string worldSeed, out string? factionName)
    {
        factionName = null;
        try
        {
            using var file = File.OpenRead(zstPath);
            using var decompressed = new DecompressionStream(file);
            using var capped = new CappedStream(decompressed, Limits.MaxSaveBytes);
            using var reader = XmlReader.Create(capped, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 0,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });

            if (!reader.ReadToFollowing("savegame")) return new SaveRejected(RefusalKeys.SavesNoRoot, "not a RimWorld save (no <savegame> root)");

            string? seed = null;
            string? scenarioDef = null;
            bool inScenario = false, inPlayerFaction = false, playerFactionFound = false;
            int factionsDepth = -1; // depth of <allFactions>; its faction <li> children sit at +1, their fields at +2
            // ReadElementContentAsString leaves the reader on the node after the element, so do not Read() again in that case.
            bool advance = true;
            while (!advance || reader.Read())
            {
                advance = true;
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Name == "scenario") inScenario = false;
                    else if (reader.Name == "allFactions") factionsDepth = -1;
                    else if (reader.Name == "li" && reader.Depth == factionsDepth + 1 && inPlayerFaction)
                    {
                        // The player faction ended without a <name>: the player has not named it yet.
                        if (seed is not null) return null;
                        inPlayerFaction = false;
                    }
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "seedString" when seed is null:
                        seed = reader.ReadElementContentAsString();
                        advance = false;
                        if (seed != worldSeed) return new SaveRejected(RefusalKeys.SavesWrongWorld, $"save belongs to another world (seed '{seed}')", seed);
                        break;
                    case "scenario":
                        inScenario = true;
                        break;
                    case "factionDef" when inScenario && scenarioDef is null:
                        scenarioDef = reader.ReadElementContentAsString();
                        advance = false;
                        break;
                    case "allFactions":
                        factionsDepth = reader.Depth;
                        break;
                    case "def" when factionsDepth >= 0 && reader.Depth == factionsDepth + 2 && !playerFactionFound:
                        string def = reader.ReadElementContentAsString();
                        advance = false;
                        if (def == scenarioDef || def is "PlayerColony" or "PlayerTribe") { inPlayerFaction = true; playerFactionFound = true; }
                        break;
                    case "name" when inPlayerFaction && reader.Depth == factionsDepth + 2:
                        factionName = reader.ReadElementContentAsString();
                        advance = false;
                        if (seed is not null) return null;
                        inPlayerFaction = false;
                        break;
                }
            }

            if (seed is null) return new SaveRejected(RefusalKeys.SavesNoSeed, "save has no world seed");
            return playerFactionFound ? null : new SaveRejected(RefusalKeys.SavesNoPlayerFaction, "save has no player faction");
        }
        catch (Exception ex) when (ex is XmlException or ZstdException or IOException or InvalidDataException or ArgumentException)
        {
            return new SaveRejected(RefusalKeys.SavesUnreadable, "save could not be read: " + ex.Message, ex.Message);
        }
    }

    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            read += n;
            if (read > limit) throw new InvalidDataException("save exceeds the size limit when decompressed");
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
