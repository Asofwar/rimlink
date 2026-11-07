using Rimlink.Protocol.V1;

namespace Rimlink.Server.Application.Outposts;

// V2 adds discriminated targets; V1 command/binding bytes keep their original interpretation.
public enum OutpostAdminRecoveryTargetKind { OrdinaryCapture = 1, CanonicalSnapshot = 2 }
public enum OutpostAdminSnapshotViewKind { DirectActual = 1, AuthorizedView = 2 }
public sealed record OutpostAdminOrdinaryInputCommitment(int Version, int InputLength, byte[] InputDigest,
    int OriginalLength, byte[] OriginalDigest, int AuthorizationLength, byte[] AuthorizationDigest);
public sealed record OutpostAdminOrdinaryTarget(string OperationId, OutpostAdminOriginalRequest Original,
    OutpostScopeMessage Scope, string ExpectedState, long Revision, string? ExistingReturnAttemptId,
    OutpostSnapshotProof RemovalSnapshot, OutpostSnapshotProof CanonicalSnapshot, OutpostAdminOrdinaryInputCommitment? Input);
public sealed record OutpostAdminCanonicalTarget(long SaveVersionId, string SnapshotId, OutpostScopeMessage OriginalScope,
    OutpostScopeMessage ViewScope, long RawLength, byte[] RawDigest, OutpostAdminSnapshotViewKind ViewKind,
    string? ViewRootId = null, byte[]? ViewRootDigest = null, byte[]? ViewPathDigest = null);
public sealed record OutpostAdminRecoveryTarget(OutpostAdminRecoveryTargetKind Kind,
    OutpostAdminOrdinaryTarget? Ordinary = null, OutpostAdminCanonicalTarget? Canonical = null);

// Ephemeral input, not part of the command, audit, URL or an administrative staging store.
public sealed record OriginalOrdinaryInput(int Version, byte[] OriginalRequest, OrdinaryOutgoingCapture Capture,
    OutpostSnapshotProof RemovalSnapshot, OutpostSnapshotProof CanonicalSnapshot);
