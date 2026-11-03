namespace Rimlink.Server.Storage.Entities;

// Durable feature history is retained after closure. IDs are opaque UUIDs, never pawn names or ThingIds.
public sealed class OutpostRow
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public int Tile { get; set; }
    public int Profile { get; set; }
    public string State { get; set; } = "Prepared";
    public bool OccupiesSlot { get; set; }
    public long Revision { get; set; }
    public int Stock { get; set; }
    public string FractionNumerator { get; set; } = "0";
    public string FractionDenominator { get; set; } = "1";
    public DateTime CheckpointUtc { get; set; }
    public string ClockProcessId { get; set; } = "";
    public long ClockTimestamp { get; set; }
    public long ClockFrequency { get; set; }
    public string ManifestId { get; set; } = "";
    public byte[] ManifestDigest { get; set; } = [];
    public string? LastTransitionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public sealed class OutpostAssignment
{
    public string Id { get; set; } = "";
    public string OutpostId { get; set; } = "";
    public string PawnCustodyId { get; set; } = "";
    // Includes reserved Prepared assignments; the unique active index prevents concurrent admission.
    public bool Active { get; set; }
    public byte[] Blob { get; set; } = [];
    public byte[] BlobDigest { get; set; } = [];
    public string FactsJson { get; set; } = "";
    public string PreparedOperationId { get; set; } = "";
    public string? ReturnBatchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

public sealed class PawnCustodyRecord
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public string LocationKind { get; set; } = "";
    public string LocationId { get; set; } = "";
    public string? AssignmentId { get; set; }
    public string FrontierTransitionId { get; set; } = "";
    public bool Tombstone { get; set; }
    public long Revision { get; set; }
}

public sealed class OutpostTransition
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Actor { get; set; } = "";
    public string? PredecessorId { get; set; }
    public string? PawnCustodyId { get; set; }
    public string? OutpostId { get; set; }
    public string OldHeadId { get; set; } = "";
    public string NextHeadId { get; set; } = "";
    public byte[] Digest { get; set; } = [];
    public byte[] Intent { get; set; } = [];
    public byte[] Effects { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class OutpostOperation
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public long SaveWriteEpoch { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Kind { get; set; } = "";
    public byte[] Intent { get; set; } = [];
    public byte[] IntentDigest { get; set; } = [];
    public byte[] Result { get; set; } = [];
    public string State { get; set; } = "";
    public string? OutpostId { get; set; }
    public string? TransitionId { get; set; }
    public long ReservedSilver { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class OutpostReservation
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public int Tile { get; set; }
    public bool Active { get; set; }
    public string OperationId { get; set; } = "";
}

public sealed class OutpostParcelLink
{
    public long ParcelId { get; set; }
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public string BatchId { get; set; } = "";
    public byte[] Payload { get; set; } = [];
    public byte[] PayloadDigest { get; set; } = [];
    public string TransitionId { get; set; } = "";
    public string? AcceptedSnapshotId { get; set; }
    public long ValueX100 { get; set; }
}

public sealed class OutpostSnapshotLink
{
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public string SnapshotId { get; set; } = "";
    public long SaveWriteEpoch { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public byte[] RawSha256 { get; set; } = [];
    public uint MetadataVersion { get; set; }
    public byte[] Metadata { get; set; } = [];
    public byte[] FrontierDigest { get; set; } = [];
    public string TransitionId { get; set; } = "";
}

public sealed class OutpostManifest
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long Revision { get; set; }
    public byte[] Digest { get; set; } = [];
    public byte[] Document { get; set; } = [];
    public bool Confirmed { get; set; }
    public bool Active { get; set; }
    public string Actor { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}

// Matching projection only: external authority is never bootstrapped from this row on startup.
public sealed class OutpostAuthority
{
    public int Id { get; set; }
    public uint FormatVersion { get; set; }
    public string InstallationId { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string RestoreBoundary { get; set; } = "";
    public string HeadId { get; set; } = "";
    public byte[] HeadDigest { get; set; } = [];
    public string? TransitionId { get; set; }
    public string Phase { get; set; } = "";
}

public sealed class OutpostRestoreAuthorization
{
    public string Id { get; set; } = "";
    public string RestoreBoundary { get; set; } = "";
    public long ParcelId { get; set; }
    public byte[] PayloadDigest { get; set; } = [];
    public string CanonicalSnapshotId { get; set; } = "";
    public byte[] CanonicalSnapshotHash { get; set; } = [];
    public string TransitionId { get; set; } = "";
}

public sealed class OutpostDeliveryAttempt
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long PlayerId { get; set; }
    public long SaveGeneration { get; set; }
    public long SaveWriteEpoch { get; set; }
    public string RestoreBoundary { get; set; } = "";
    public long ParcelId { get; set; }
    public string RequestId { get; set; } = "";
    public byte[] IntentDigest { get; set; } = [];
    public byte[] PayloadDigest { get; set; } = [];
    public string CanonicalSnapshotId { get; set; } = "";
    public byte[] CanonicalSnapshotHash { get; set; } = [];
    public string State { get; set; } = "";
    public bool Active { get; set; }
    public string TransitionId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class OutpostLifecycleOperation
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Kind { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string RestoreBoundary { get; set; } = "";
    public string Phase { get; set; } = "";
    public byte[] Intent { get; set; } = [];
    public byte[] IntentDigest { get; set; } = [];
    public byte[] Progress { get; set; } = [];
    public byte[] Result { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
