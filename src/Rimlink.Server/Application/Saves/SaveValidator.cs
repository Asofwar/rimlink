using System.Xml;
using Rimlink.Protocol;
using ZstdSharp;

namespace Rimlink.Server.Application.Saves;

/// <summary>
/// Checks that a zstd-compressed upload is a RimWorld save for this world and this player:
/// root &lt;savegame&gt;, the first &lt;seedString&gt; equals the world seed, and the player faction's (PlayerColony or PlayerTribe) &lt;name&gt; equals the player's faction.
/// Reads at most <see cref="Limits.MaxSaveBytes"/> of decompressed data. Returns null when valid, otherwise the reason.
/// </summary>
public static class SaveValidator
{
    public static string? Validate(string zstPath, string worldSeed, string factionName)
    {
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

            if (!reader.ReadToFollowing("savegame")) return "not a RimWorld save (no <savegame> root)";

            string? seed = null;
            string? playerFaction = null;
            string? pendingDef = null;
            // ReadElementContentAsString leaves the reader on the node after the element, so do not Read() again in that case.
            bool advance = true;
            while (!advance || reader.Read())
            {
                advance = true;
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "seedString" when seed is null:
                        seed = reader.ReadElementContentAsString();
                        advance = false;
                        if (seed != worldSeed) return $"save belongs to another world (seed '{seed}')";
                        break;
                    case "li" when playerFaction is null:
                        pendingDef = null;
                        break;
                    case "def" when playerFaction is null:
                        pendingDef = reader.ReadElementContentAsString();
                        advance = false;
                        break;
                    case "name" when playerFaction is null && pendingDef is "PlayerColony" or "PlayerTribe":
                        playerFaction = reader.ReadElementContentAsString();
                        advance = false;
                        if (playerFaction != factionName) return $"save belongs to another faction ('{playerFaction}')";
                        break;
                }
                if (seed is not null && playerFaction is not null) return null;
            }

            if (seed is null) return "save has no world seed";
            return "save has no player faction";
        }
        catch (Exception ex) when (ex is XmlException or ZstdException or IOException or InvalidDataException or ArgumentException)
        {
            return "save could not be read: " + ex.Message;
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
