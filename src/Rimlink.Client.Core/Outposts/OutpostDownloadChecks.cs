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
        RollbackBridgeDecision Refuse() => new(false, header.OutpostRestoreBridge is null
            ? "Rimlink.Outposts.RollbackIncompatible" : "Rimlink.Outposts.RestoreIncompatible", []);
        int active = (header.OutpostRollbackBridge is null ? 0 : 1) + (header.OutpostRestoreBridge is null ? 0 : 1)
            + (header.OutpostRollbackFromViewBridge is null ? 0 : 1);
        if (active > 1) return Refuse();
        try
        {
            recovery.Validate();
            if (rawHash.Length != 32 || recovery.SnapshotId != header.SnapshotId || recovery.Version != header.RecoveryVersion
                || recovery.Generation != header.SaveGeneration) return Refuse();
            if (header.GuildVersion != (recovery.Outposts.Metadata.GuildMetadata?.SchemaVersion ?? 0)
                || header.OutpostMetadata?.GuildMetadata is not null && !header.OutpostMetadata.Equals(recovery.Outposts.Metadata)) return Refuse();
            if (!recovery.Outposts.Present) return active == 0 ? new(true, null, []) : Refuse();
            var metadata = recovery.Outposts.Metadata;
            var view = header.OutpostRestoreBridge is { } restore ? OutpostSourceGraph.ValidateRoot(restore)
                : header.OutpostRollbackFromViewBridge is { } rollbackView ? OutpostSourceGraph.ValidateRoot(rollbackView) : null;
            var current = view?.EffectiveScope ?? header.OutpostRollbackBridge?.CurrentScope ?? metadata.Scope;
            if (authentication?.Player is null || recovery.Outposts.RequiresPersonalFamily && authentication.OutpostVersion != 1
                || metadata.GuildMetadata is not null && (authentication.GuildVersion != GuildResourceContracts.CurrentVersion
                    || header.GuildVersion != GuildResourceContracts.CurrentVersion || !metadata.Equals(header.OutpostMetadata))
                || authentication.Player.PlayerId != current.OwnerPlayerId
                || authentication.SaveGeneration != current.SaveGeneration || authentication.SaveWriteEpoch != current.SaveWriteEpoch
                || authentication.OutpostRestoreBoundary != current.RestoreBoundary || header.SaveGeneration != current.SaveGeneration
                || header.SaveWriteEpoch != current.SaveWriteEpoch) return Refuse();
            if (active == 0)
                return recovery.WriteEpoch == header.SaveWriteEpoch ? new(true, null, metadata.Registry.Select(r => r.Clone()).ToArray()) : Refuse();
            var frontier = new OutpostSaveMetadata { Frontier = { metadata.Frontier } };
            byte[] frontierHash = SHA256.HashData(frontier.ToByteArray());
            var actual = new OutpostSnapshotProof { Scope = metadata.Scope.Clone(), SnapshotId = recovery.SnapshotId, RawSha256 = ByteString.CopyFrom(rawHash) };
            if (view is not null)
            {
                if (header.OutpostVersion != 1 || header.OutpostRestoreBoundary != current.RestoreBoundary) return Refuse();
                var graph = header.OutpostRestoreBridge?.SourceProvenance ?? header.OutpostRollbackFromViewBridge?.SourceProvenance;
                var canonical = header.OutpostRestoreBridge?.CanonicalSnapshot ?? header.OutpostRollbackFromViewBridge?.CanonicalSnapshot;
                if (graph is null || canonical is null || !canonical.Equals(actual)) return Refuse();
                var source = graph.ActualSources.Single(s => s.Snapshot.SnapshotId == actual.SnapshotId);
                if (!frontierHash.AsSpan().SequenceEqual(source.LocalFrontierDigest.Span)) return Refuse();
                OutpostSourceGraph.ValidateEmbeddedHistory(graph, actual, metadata);
                SourceBridgeDecision result;
                if (header.OutpostRestoreBridge is { } root)
                    result = OutpostSourceBridgeChecks.Validate(root, actual, current, metadata.Registry);
                else if (header.OutpostRollbackFromViewBridge is { } fromView)
                    result = OutpostSourceBridgeChecks.Validate(fromView, actual, current, metadata.Registry);
                else return Refuse();
                return new(result.Allowed, result.RefusalKey, result.Registry);
            }
            if (header.OutpostRollbackBridge is not { } bridge) return Refuse();
            if (bridge.SourceProvenance is not null || bridge.SourcePathDigest.Length != 0)
            {
                _ = OutpostSourceGraph.ValidateRoot(bridge);
                if (!bridge.CanonicalSnapshot.Equals(actual)) return Refuse();
                if (bridge.SourceProvenance is not { } graph) return Refuse();
                OutpostSourceGraph.ValidateEmbeddedHistory(graph, actual, metadata);
            }
            else if (metadata.RestoreBridge is not null || metadata.RollbackFromViewBridge is not null
                || metadata.RollbackBridge?.SourceProvenance is not null) return Refuse();
            if (!metadata.Scope.Equals(bridge.SourceScope) || !frontierHash.AsSpan().SequenceEqual(bridge.LocalFrontierDigest.Span)) return Refuse();
            return OutpostRollbackBridgeChecks.Validate(bridge, actual, current, metadata.Registry);
        }
        catch (InvalidDataException) { return Refuse(); }
    }
}
