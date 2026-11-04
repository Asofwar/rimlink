using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostSnapshotGuard
{
    private async Task ValidateMutationsStoredAsync(Dictionary<string, OutpostTransition> transitions, List<OutpostSnapshotLink> links, CancellationToken ct)
    {
        foreach (var operation in await db.OutpostOperations.AsNoTracking().Where(o => o.Kind != "Found" && o.Kind != "Assign").ToListAsync(ct))
        {
            if (operation.Intent.Length is < 1 or > Limits.MaxMessageBytes || operation.Result.Length is < 1 or > Limits.MaxMessageBytes)
                throw Unavailable("required-mutation-evidence-unavailable");
            var intent = OutpostMutationRequest.Parser.ParseFrom(operation.Intent);
            var reply = OutpostOperationReply.Parser.ParseFrom(operation.Result);
            if (intent.Kind is not (OutpostMutationKind.Claim or OutpostMutationKind.Recall or OutpostMutationKind.Close or OutpostMutationKind.Switch)
                || operation.Kind != intent.Kind.ToString() || intent.Scope is null || intent.Scope.WorldInstanceId != operation.WorldId
                || intent.Scope.OwnerPlayerId != operation.PlayerId || intent.Scope.SaveGeneration != operation.SaveGeneration
                || intent.Scope.SaveWriteEpoch != operation.SaveWriteEpoch || intent.Scope.RestoreBoundary != operation.RestoreBoundary
                || intent.ClientRequestId != operation.RequestId || intent.OutpostId != operation.OutpostId || operation.State != "Committed" || operation.ReservedSilver != 0
                || !OutpostService.Digest(intent).Equals(intent.IntentDigest) || !intent.IntentDigest.Span.SequenceEqual(operation.IntentDigest)
                || !transitions.TryGetValue(operation.TransitionId ?? "", out var transition) || transition.Id != operation.Id || transition.Kind != operation.Kind
                || transition.WorldId != operation.WorldId || transition.PlayerId != operation.PlayerId || transition.SaveGeneration != operation.SaveGeneration
                || transition.RestoreBoundary != operation.RestoreBoundary || !transition.Intent.AsSpan().SequenceEqual(operation.Intent)
                || reply.TransitionId != transition.Id || reply.TransitionDigest.Length != 0 || !reply.Scope.Equals(intent.Scope)
                || reply.Status != OutpostOperationStatus.Committed || reply.ClientRequestId != operation.RequestId || !reply.IntentDigest.Equals(intent.IntentDigest)
                || reply.AuthoritativeRevision != intent.ExpectedRevision + 1 || reply.PreparedIntent is not null
                || !links.Any(l => l.WorldId == operation.WorldId && l.PlayerId == operation.PlayerId && l.SaveGeneration == operation.SaveGeneration
                    && l.SnapshotId == intent.CanonicalSnapshot?.SnapshotId && l.SaveWriteEpoch == operation.SaveWriteEpoch && l.RestoreBoundary == operation.RestoreBoundary
                    && l.RawSha256.AsSpan().SequenceEqual(intent.CanonicalSnapshot.RawSha256.Span)))
                throw Unavailable("required-mutation-evidence-unavailable");
            var parcels = await db.OutpostParcelLinks.AsNoTracking().Where(p => p.BatchId == operation.Id).OrderBy(p => p.ParcelId).ToListAsync(ct);
            if (!parcels.Select(p => p.ParcelId).SequenceEqual(reply.ParcelIds)) throw Unavailable("required-return-batch-evidence-unavailable");
            for (int i = 0; i < parcels.Count; i++)
            {
                var link = parcels[i]; var payload = OutpostParcelPayload.Parser.ParseFrom(link.Payload);
                if (link.TransitionId != operation.Id || payload.BatchOperationId != operation.Id || !payload.Scope.Equals(intent.Scope)
                    || payload.OutpostId != operation.OutpostId || payload.ManifestId != intent.ManifestId || !payload.ManifestDigest.Equals(intent.ManifestDigest)
                    || payload.ChildIndex != i || payload.ChildCount != parcels.Count)
                    throw Unavailable("required-return-batch-evidence-unavailable");
            }
        }
    }
}
