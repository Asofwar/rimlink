using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Application.Outposts;

public enum OutpostAdminActionKind { Initialize = 1, ActivateDomainFormat = 2, ConfirmManifest = 3, Resolve = 4 }
public enum OutpostAuthorityObservationKind { Committed = 1, Uninitialized = 2, Unavailable = 3 }
public enum OutpostAdminResolutionKind { ExistingTerminal = 1, AbortNoRemoval = 2, AbortWithReturn = 3, OrdinaryReturn = 4, CanonicalDownload = 5 }
public enum OutpostAdminOperationFamily { Outpost = 1, Ordinary = 2 }
public enum OutpostAdminEvidenceKind { Transition = 1, Snapshot = 2, Operation = 3, Parcel = 4, Manifest = 5, Custody = 6, OrdinarySource = 7 }

public sealed record OutpostSchemaBinding(string ProfileId, byte[] Digest);
public sealed record OutpostCommittedObservation(string Installation, string World, string Boundary,
    string HeadId, byte[] HeadDigest, string TransitionId, OutpostDomainFormat DomainFormat, OutpostSchemaBinding Schema);
public sealed record OutpostUninitializedObservation(string World, byte[] WorldBindingDigest,
    byte[] QualificationDigest, OutpostSchemaBinding Schema);
public sealed record OutpostUnavailableObservation(string Reason);
public sealed record OutpostAuthorityObservation(OutpostAuthorityObservationKind Kind,
    OutpostCommittedObservation? Committed = null, OutpostUninitializedObservation? Uninitialized = null,
    OutpostUnavailableObservation? Unavailable = null);

public sealed record OutpostAdminEvidenceReference(OutpostAdminEvidenceKind Kind, string Id, byte[]? Digest);
public sealed record OutpostAdminAllocation(string ReceiptId, string TransitionId, string NextHeadId, long AuditId, DateTime At);
public sealed record OutpostAdminInitializeSelection(string Installation, string Boundary, OutpostSchemaBinding Schema);
public sealed record OutpostAdminActivationSelection(OutpostDomainFormat From, OutpostDomainFormat To,
    byte[] PreviousDomainDigest, OutpostSchemaBinding SourceSchema, OutpostSchemaBinding TargetSchema, string MigrationId);
public sealed record OutpostAdminManifestSelection(string ManifestId, long Revision, byte[] Digest, byte[] AffectedStateDigest);
public sealed record OutpostAdminOriginalRequest(OutpostAdminOperationFamily Family, string Kind, string RequestId,
    string? LocalOperationId, byte[] IntentDigest, OrdinaryDerivedRoute? DerivedRoute);
public sealed record OutpostAdminResolutionSelection(OutpostAdminResolutionKind Kind, string OperationId,
    OutpostAdminOriginalRequest Original, OutpostScopeMessage Scope, string ExpectedState, long Revision,
    OutpostSnapshotProof? CanonicalSnapshot);

/// <summary>Only a transport-authenticated server actor may populate Actor; incoming drafts do not authenticate themselves.</summary>
public sealed record OutpostAdminIntent(int Version, Actor Actor, string RequestId, string Reason,
    OutpostAuthorityObservation Expected, OutpostAdminAllocation Allocation, OutpostAdminActionKind Action,
    OutpostAdminEvidenceReference[] Evidence, OutpostAdminInitializeSelection? Initialize = null,
    OutpostAdminActivationSelection? Activation = null, OutpostAdminManifestSelection? Manifest = null,
    OutpostAdminResolutionSelection? Resolution = null, OutpostAdminRecoveryTarget? Recovery = null);

public sealed record OutpostAdminCommand(OutpostAdminIntent Intent, byte[] IntentDigest, bool Confirmed);
