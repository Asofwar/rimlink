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
        var source = await new OutpostRestoreEvidence(db, files).ResolveRollbackSourceAsync(target, currentScope, ct);
        // This is a read-only authorized view for compatibility checks, never a replacement actual snapshot proof.
        await ValidateCausalityAsync(source.ValidationMetadata, ct);
        await ValidateBranchAsync(source.ValidationMetadata, ct);
    }
}
