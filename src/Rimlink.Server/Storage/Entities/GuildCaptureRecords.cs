using Rimlink.Server.Application.GuildCapture;

namespace Rimlink.Server.Storage.Entities;

public sealed class GuildOutpostRow
{
    public string Id { get; set; } = "";
    public string WorldId { get; set; } = "";
    public int Tile { get; set; }
    public GuildResource Resource { get; set; }
    public long? AllianceId { get; set; }
    public long Revision { get; set; }
    public int Stock { get; set; }
    public long Remainder { get; set; }
    public DateTime CheckpointUtc { get; set; }
    public string ClockProcessId { get; set; } = "";
    public long ClockTimestamp { get; set; }
    public long ClockFrequency { get; set; }
    public string Phase { get; set; } = "Neutral";
    public int DefenceHourUtc { get; set; }
    public int? PendingDefenceHourUtc { get; set; }
    public DateTime? WindowChangeAtUtc { get; set; }
    public DateTime ProtectedUntilUtc { get; set; }
    public int ConsecutiveAborts { get; set; }
}

public sealed class GuildCaptureConsent
{
    public string Id { get; set; } = "";
    public string OutpostId { get; set; } = "";
    public long AllianceId { get; set; }
    public long LeaderId { get; set; }
    public long OwnerRevision { get; set; }
    public int RulesVersion { get; set; }
    public int DefenceHourUtc { get; set; }
    public byte[] Digest { get; set; } = [];
    public DateTime AcceptedAtUtc { get; set; }
}

public sealed class GuildResourceObligation
{
    public string Id { get; set; } = "";
    public string OutpostId { get; set; } = "";
    public long OwnerRevision { get; set; }
    public string? CampaignId { get; set; }
    public string TransitionId { get; set; } = "";
    public long AllianceId { get; set; }
    public GuildResource Resource { get; set; }
    public int OriginalCount { get; set; }
    public int RemainingCount { get; set; }
    public int ReservedCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class GuildResourceClaim
{
    public string Id { get; set; } = "";
    public string RequestId { get; set; } = "";
    public byte[] IntentDigest { get; set; } = [];
    public long ActorId { get; set; }
    public long AllianceId { get; set; }
    public string OriginKind { get; set; } = "";
    public string OriginId { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string Boundary { get; set; } = "";
    public long RecipientId { get; set; }
    public long SaveGeneration { get; set; }
    public long SaveWriteEpoch { get; set; }
    public GuildResource Resource { get; set; }
    public int Count { get; set; }
    public string ManifestId { get; set; } = "";
    public long ValuationRevision { get; set; }
    public string TransitionId { get; set; } = "";
    public string State { get; set; } = "Pending";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class GuildParcelLink
{
    public long ParcelId { get; set; }
    public string ClaimId { get; set; } = "";
    public byte[] Payload { get; set; } = [];
    public byte[] PayloadDigest { get; set; } = [];
    public long ValueX100 { get; set; }
    public string? AcceptedSnapshotId { get; set; }
}

public sealed class GuildDeliveryAttempt
{
    public string Id { get; set; } = "";
    public long ParcelId { get; set; }
    public string RequestId { get; set; } = "";
    public byte[] IntentDigest { get; set; } = [];
    public long RecipientId { get; set; }
    public string WorldId { get; set; } = "";
    public string Boundary { get; set; } = "";
    public long SaveGeneration { get; set; }
    public long SaveWriteEpoch { get; set; }
    public string BeforeSnapshotId { get; set; } = "";
    public byte[] BeforeSnapshotDigest { get; set; } = [];
    public string? AcceptedSnapshotId { get; set; }
    public byte[]? Receipt { get; set; }
    public bool Active { get; set; }
}

public sealed class GuildCampaign
{
    public string Id { get; set; } = "";
    public string OutpostId { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string Boundary { get; set; } = "";
    public long OwnerRevision { get; set; }
    public long AttackerAllianceId { get; set; }
    public long DefenderAllianceId { get; set; }
    public long AttackerCommanderId { get; set; }
    public long? DefenderCommanderId { get; set; }
    public DateTime DeclaredAtUtc { get; set; }
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime? ProductionCutUtc { get; set; }
    public byte[] RulesDigest { get; set; } = [];
    public string Phase { get; set; } = "Scheduled";
    public string? TerminalReason { get; set; }
    public bool Active { get; set; }
}

public sealed class GuildAcquisitionReservation
{
    public string Id { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public long AllianceId { get; set; }
    public bool Active { get; set; }
}

public sealed class GuildBattleSlot
{
    public string Id { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string ExecutorId { get; set; } = "";
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public bool Active { get; set; }
}

public sealed class GuildBattle
{
    public string Id { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string Boundary { get; set; } = "";
    public string ExecutorId { get; set; } = "";
    public long Attempt { get; set; }
    public long Fence { get; set; }
    public byte[] StartNonce { get; set; } = [];
    public byte[]? StartManifest { get; set; }
    public byte[]? StartDigest { get; set; }
    public byte[]? FinalManifest { get; set; }
    public byte[]? FinalDigest { get; set; }
    public byte[]? Receipt { get; set; }
    // Only the immutable terminal view belongs to the financial cut; live views remain transport state.
    public byte[]? View { get; set; }
    public long ViewSequence { get; set; }
    public string Phase { get; set; } = "Prepared";
    public DateTime? StartedAtUtc { get; set; }
    public bool Active { get; set; }
}

public sealed class GuildBattleArtifact
{
    public string Id { get; set; } = "";
    public string BattleId { get; set; } = "";
    public string Kind { get; set; } = "";
    public byte[] Digest { get; set; } = [];
    public long SizeBytes { get; set; }
    public string RelativePath { get; set; } = "";
}

public sealed class GuildBattleControlGrant
{
    public string Id { get; set; } = "";
    public string BattleId { get; set; } = "";
    public long PlayerId { get; set; }
    public int Side { get; set; }
    public long Epoch { get; set; }
    public byte[] CapabilityDigest { get; set; } = [];
    public DateTime ExpiresAtUtc { get; set; }
    public bool Active { get; set; }
}

public sealed class GuildCaptureOperation
{
    public string Id { get; set; } = "";
    public long ActorId { get; set; }
    public string RequestId { get; set; } = "";
    public byte[] IntentDigest { get; set; } = [];
    public string Kind { get; set; } = "";
    public byte[] Receipt { get; set; } = [];
    public string TransitionId { get; set; } = "";
}
