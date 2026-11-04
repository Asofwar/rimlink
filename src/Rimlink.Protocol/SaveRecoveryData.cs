using System.Security.Cryptography;
using System.Xml;

namespace Rimlink.Protocol;

/// <summary>Proofs written with one colony snapshot, read from that snapshot rather than a later login or game.</summary>
public sealed record SaveRecoveryData
{
    public const uint CurrentVersion = 1;
    public const string ComponentClass = "Rimlink.Client.Game.RimlinkGameComponent";
    public const string VersionNode = "rimlinkRecoveryVersion", SnapshotNode = "rimlinkSnapshotId", EpochNode = "rimlinkSaveWriteEpoch";
    public const string ParcelsNode = "rimlinkParcelReceipts", RemovedNode = "rimlinkRemovedOperations", ReturnedNode = "rimlinkReturnedOperations";
    public OutpostRecoveryData Outposts { get; init; } = new();
    public uint Version { get; init; }
    public string SnapshotId { get; init; } = "";
    public long Generation { get; init; }
    public long WriteEpoch { get; init; }
    public long[] ParcelIds { get; init; } = [];
    public string[] Removed { get; init; } = [];
    public string[] Returned { get; init; } = [];

    public void Validate()
    {
        Outposts.Validate();
        if (Outposts.Present && (Version != CurrentVersion || Outposts.Metadata.Scope.SaveGeneration != Generation || Outposts.Metadata.Scope.SaveWriteEpoch != WriteEpoch))
            throw new InvalidDataException("outpost recovery scope differs from snapshot identity");
        if (Version == 0 && SnapshotId.Length == 0 && ParcelIds.Length + Removed.Length + Returned.Length == 0) return;
        if (Version != CurrentVersion || !Guid.TryParseExact(SnapshotId, "N", out _) || Generation < 0 || WriteEpoch < 0)
            throw new InvalidDataException("invalid save recovery identity");
        if (ParcelIds.Length > 100_000 || Removed.Length > 100_000 || Returned.Length > 100_000
            || ParcelIds.Any(id => id <= 0) || ParcelIds.Distinct().Count() != ParcelIds.Length
            || Removed.Any(id => !Guid.TryParseExact(id, "N", out _)) || Removed.Distinct(StringComparer.Ordinal).Count() != Removed.Length
            || Returned.Any(id => !Guid.TryParseExact(id, "N", out _)) || Returned.Distinct(StringComparer.Ordinal).Count() != Returned.Length)
            throw new InvalidDataException("invalid save recovery receipts");
    }

    public static SaveRecoveryData ReadXml(Stream xml, out byte[] rawHash)
    {
        using var hashed = new HashedInput(xml);
        using var reader = XmlReader.Create(hashed, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true });
        SaveRecoveryData? result = null;
        bool game = false, components = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == 1 && reader.Name == "game") game = false;
                if (reader.Depth == 2 && reader.Name == "components") components = false;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth == 0 && reader.Name != "savegame") throw new InvalidDataException("not a RimWorld save");
            if (reader.Depth == 1 && reader.Name == "game") game = true;
            if (game && reader.Depth == 2 && reader.Name == "components") components = true;
            if (!components || reader.Depth != 3 || reader.Name != "li" || reader.GetAttribute("Class") != ComponentClass) continue;
            if (result is not null) throw new InvalidDataException("duplicate Rimlink save component");
            using var subtree = reader.ReadSubtree();
            result = ReadComponent(subtree);
        }
        // Read to EOF even after finding the component: the raw hash and validation cover the complete snapshot.
        rawHash = hashed.Hash.GetHashAndReset();
        result ??= new SaveRecoveryData();
        result.Validate();
        return result;
    }

    private static SaveRecoveryData ReadComponent(XmlReader reader)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, string[]>(StringComparer.Ordinal);
        OutpostRecoveryData? outposts = null;
        bool advance = true;
        while (!advance || reader.Read())
        {
            advance = true;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1) continue;
            string name = reader.Name;
            if (name == OutpostRecoveryData.SectionNode)
            {
                if (outposts is not null) throw new InvalidDataException("duplicate outpost recovery section");
                outposts = OutpostRecoveryData.ReadSection(reader);
            }
            else if (name is VersionNode or SnapshotNode or EpochNode or "rimlinkSaveGeneration")
            {
                if (!values.TryAdd(name, reader.ReadElementContentAsString())) throw new InvalidDataException("duplicate recovery field");
                advance = false;
            }
            else if (name is ParcelsNode or RemovedNode or ReturnedNode)
            {
                using var subtree = reader.ReadSubtree();
                var entries = new List<string>();
                bool next = true;
                while (!next || subtree.Read())
                {
                    next = true;
                    if (subtree.NodeType != XmlNodeType.Element || subtree.Depth != 1) continue;
                    if (subtree.Name != "li" || entries.Count >= 100_000) throw new InvalidDataException("invalid recovery list");
                    entries.Add(subtree.ReadElementContentAsString()); next = false;
                }
                if (!lists.TryAdd(name, entries.ToArray())) throw new InvalidDataException("duplicate recovery list");
            }
        }
        try
        {
            return new SaveRecoveryData
            {
                Outposts = outposts ?? new(),
                Version = values.TryGetValue(VersionNode, out var version) ? uint.Parse(version, System.Globalization.CultureInfo.InvariantCulture) : 0,
                SnapshotId = values.GetValueOrDefault(SnapshotNode, ""),
                Generation = values.TryGetValue("rimlinkSaveGeneration", out var generation) ? long.Parse(generation, System.Globalization.CultureInfo.InvariantCulture) : 0,
                WriteEpoch = values.TryGetValue(EpochNode, out var epoch) ? long.Parse(epoch, System.Globalization.CultureInfo.InvariantCulture) : 0,
                ParcelIds = lists.GetValueOrDefault(ParcelsNode, []).Select(id => long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                Removed = lists.GetValueOrDefault(RemovedNode, []), Returned = lists.GetValueOrDefault(ReturnedNode, []),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new InvalidDataException("invalid recovery number", ex); }
    }

    private sealed class HashedInput(Stream inner) : Stream
    {
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int length = inner.Read(buffer, offset, count);
            read += length;
            if (read > Limits.MaxSaveBytes) throw new InvalidDataException("save exceeds the decompressed size limit");
            Hash.AppendData(buffer, offset, length); return length;
        }
        protected override void Dispose(bool disposing) { if (disposing) Hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
