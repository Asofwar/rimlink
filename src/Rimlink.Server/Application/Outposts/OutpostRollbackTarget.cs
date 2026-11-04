using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostSnapshotGuard
{
    internal async Task ValidateRollbackTargetHeldAsync(OutpostLease lease, long playerId, SaveVersion target,
        OutpostScopeMessage currentScope, CancellationToken ct)
    {
        RequireOwner(lease, playerId);
        if (target.SnapshotId is null || target.RawSha256?.Length != 32) throw Unavailable("rollback-target-proof-missing");
        var original = currentScope.Clone(); original.SaveWriteEpoch = target.SaveWriteEpoch;
        var link = await new OutpostLifecycleEvidence(db, files).RollbackLinkAsync(new()
        { Scope = original, SnapshotId = target.SnapshotId, RawSha256 = ByteString.CopyFrom(target.RawSha256) }, ct);
        byte[] bytes = files.ReadEvidence(target);
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(bytes, false), out byte[] rawHash);
        if (!rawHash.AsSpan().SequenceEqual(link.RawSha256) || recovery.SnapshotId != target.SnapshotId
            || recovery.Generation != target.SaveGeneration || recovery.WriteEpoch != target.SaveWriteEpoch
            || !recovery.Outposts.Present || !recovery.Outposts.Metadata.Scope.Equals(original)
            || !recovery.Outposts.Metadata.ToByteArray().AsSpan().SequenceEqual(link.Metadata))
            throw Unavailable("rollback-target-bytes-mismatch");
        // The same read-only lineage checks as a new snapshot: stock/clock is deliberately outside this local frontier.
        await ValidateCausalityAsync(recovery.Outposts.Metadata, ct);
        await ValidateBranchAsync(recovery.Outposts.Metadata, ct);
    }
}
