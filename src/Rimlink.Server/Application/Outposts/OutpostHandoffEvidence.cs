using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostSnapshotGuard
{
    private async Task<bool> IncomingHandoffAsync(OutpostSaveMetadata metadata, PawnRegistryEnvelope envelope, PawnCustodyRecord custody, CancellationToken ct)
    {
        if (envelope.Tombstone || envelope.NonAuthoritativeMirror || !envelope.Scope.Equals(metadata.Scope)
            || envelope.AssignmentId != (custody.AssignmentId ?? "") || custody.LocationKind != "TransferPending"
            || custody.WorldId != metadata.Scope.WorldInstanceId || custody.PlayerId == metadata.Scope.OwnerPlayerId) return false;
        foreach (var receipt in metadata.Receipts.Where(r => r.PawnCustodyIds.Contains(custody.Id) && r.DeliveryAttemptId.Length > 0))
        {
            var attempt = await db.OutpostDeliveryAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == receipt.DeliveryAttemptId, ct);
            if (attempt is null || !attempt.Active || attempt.State != "Started" || attempt.PlayerId != metadata.Scope.OwnerPlayerId
                || custody.LocationId != attempt.ParcelId.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
            var link = await db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == attempt.ParcelId, ct);
            var source = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == link.TransitionId, ct);
            if (await PawnCustodyBridge.IsHandoffSourceAsync(db, source, link, ct)) return true;
        }
        return false;
    }
}
