namespace Rimlink.Server.Storage.Entities;

// Permanent negative source fence, distinct from mail and assignment operations.
public sealed class OutpostOrdinaryReturn
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public long SaveWriteEpoch { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public int RequestKind { get; set; }
    public string RequestId { get; set; } = "";
    public string LocalOperationId { get; set; } = "";
    public int DerivedRequestKind { get; set; }
    public string DerivedRequestId { get; set; } = "";
    public byte[] Intent { get; set; } = [];
    public byte[] Payload { get; set; } = [];
    public byte[] PayloadDigest { get; set; } = [];
    public string State { get; set; } = "Started";
    public string? AcceptedSnapshotId { get; set; }
}
