using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Outposts;

/// <summary>Checks the authenticated header against the exact downloaded recovery bytes before publishing a file.</summary>
public static class OutpostDownloadChecks
{
    public static RollbackBridgeDecision Validate(SaveHeader header, SaveRecoveryData recovery, byte[] rawHash, LoginReply? authentication)
    {
        RollbackBridgeDecision Refuse() => new(false, "Rimlink.Outposts.RollbackIncompatible", []);
        try { recovery.Validate(); } catch (InvalidDataException) { return Refuse(); }
        if (rawHash.Length != 32 || recovery.SnapshotId != header.SnapshotId || recovery.Version != header.RecoveryVersion
            || recovery.Generation != header.SaveGeneration) return Refuse();
        if (!recovery.Outposts.Present) return header.OutpostRollbackBridge is null ? new(true, null, []) : Refuse();
        var metadata = recovery.Outposts.Metadata;
        var current = header.OutpostRollbackBridge?.CurrentScope ?? metadata.Scope;
        if (authentication?.Player is null || authentication.OutpostVersion != 1 || authentication.Player.PlayerId != current.OwnerPlayerId
            || authentication.SaveGeneration != current.SaveGeneration || authentication.SaveWriteEpoch != current.SaveWriteEpoch
            || authentication.OutpostRestoreBoundary != current.RestoreBoundary || header.SaveGeneration != current.SaveGeneration
            || header.SaveWriteEpoch != current.SaveWriteEpoch) return Refuse();
        if (header.OutpostRollbackBridge is not { } bridge)
            return recovery.WriteEpoch == header.SaveWriteEpoch ? new(true, null, metadata.Registry.Select(r => r.Clone()).ToArray()) : Refuse();
        if (!metadata.Scope.Equals(bridge.SourceScope)) return Refuse();
        var frontier = new OutpostSaveMetadata { Frontier = { metadata.Frontier } };
        if (!SHA256.HashData(frontier.ToByteArray()).AsSpan().SequenceEqual(bridge.LocalFrontierDigest.Span)) return Refuse();
        return OutpostRollbackBridgeChecks.Validate(bridge,
            new() { Scope = metadata.Scope.Clone(), SnapshotId = recovery.SnapshotId, RawSha256 = ByteString.CopyFrom(rawHash) }, current, metadata.Registry);
    }
}
