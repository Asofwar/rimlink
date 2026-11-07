using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAssignmentService
{
    internal static async Task<OutpostAdminCancellationEvidence> StageAdminCancellationHeldAsync(RimlinkDbContext db, SaveFiles files,
        OutpostLease lease, OutpostAdminResolutionSelection selected, string id, DateTime at, CancellationToken ct,
        OutpostAuthority.OutpostDomainTransaction? domain = null)
    {
        if (domain is null) throw new InvalidOperationException("outpost-cancellation-requires-owned-transaction");
        domain.RequireAdminStage(db, lease, id, at, "AdminResolved");
        if (!lease.CoversOwner(selected.Scope.OwnerPlayerId)) throw new InvalidOperationException("outpost-cancellation-owner-not-held");
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("outpost-cancellation-requires-owned-transaction");
        var source = await OutpostAdminResolveEvidence.ReadCancellationAsync(db, files, selected, current: true, ct);
        var outpost = await db.Outposts.AsNoTracking().SingleAsync(o => o.Id == source.Operation.OutpostId, ct);
        await StageCancellationCoreAsync(db, lease, source.Operation, source.Intent, outpost, source.NoRemoval, id, at, ct);
        // Materialize the actual parcel-link result inside the caller transaction before freezing its descriptor.
        await db.SaveChangesAsync(ct);
        return source;
    }

    private static async Task StageCancellationCoreAsync(RimlinkDbContext db, OutpostLease lease, OutpostOperation operation,
        OutpostPrepareRequest intent, OutpostRow outpost, bool noRemoval, string id, DateTime at, CancellationToken token)
    {
        if (!lease.CoversOwner(operation.PlayerId)) throw new InvalidOperationException("outpost-cancellation-owner-not-held");
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("outpost-cancellation-requires-owned-transaction");
        long revision = checked(outpost.Revision + 1); var parcelIds = new List<long>();
            long parcelId = 0;
            if (!noRemoval)
            {
                var pawns = new PawnList();
                foreach (var worker in intent.Workers)
                {
                    var pawn = worker.Pawn.Clone(); pawn.Registry.AssignmentId = worker.AssignmentId; pawn.Registry.Tombstone = false;
                    pawns.Pawns.Add(pawn);
                }
                var parcel = new Parcel { ToPlayerId = operation.PlayerId, Kind = ParcelKindDb.LotReturn, Pawns = pawns.ToByteArray(),
                    SizeBytes = pawns.CalculateSize(), CreatedAt = at, ClientRequestId = operation.RequestId, RequestKind = "outpost-return" };
                db.Parcels.Add(parcel); await db.SaveChangesAsync(token); parcelId = parcel.Id; parcelIds.Add(parcelId);
                var manifest = await db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Id == intent.ManifestId, token);
                var payload = new OutpostParcelPayload { SchemaVersion = 1, Kind = OutpostPayloadKind.Workers, Scope = intent.Scope.Clone(),
                    OutpostId = intent.OutpostId, BatchOperationId = id, ManifestId = intent.ManifestId, ManifestDigest = intent.ManifestDigest,
                    ValuationRevision = manifest.Revision, ChildCount = 1 };
                payload.PawnCustodyIds.Add(intent.Workers.Select(w => w.PawnCustodyId)); payload.AssignmentIds.Add(intent.Workers.Select(w => w.AssignmentId));
                OutpostParcelContents.Bind(payload, null, pawns, parcel.Silver);
                byte[] bytes = payload.ToByteArray();
                db.OutpostParcelLinks.Add(new() { ParcelId = parcelId, WorldId = operation.WorldId, PlayerId = operation.PlayerId,
                    SaveGeneration = operation.SaveGeneration, RestoreBoundary = operation.RestoreBoundary, BatchId = id,
                    Payload = bytes, PayloadDigest = SHA256.HashData(bytes), TransitionId = id,
                    ValueX100 = intent.Workers.Sum(w => w.Pawn.MarketValueX100) });
            }
            foreach (var worker in intent.Workers)
            {
                await db.OutpostAssignments.Where(a => a.Id == worker.AssignmentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Active, false)
                    .SetProperty(a => a.EndedAt, at).SetProperty(a => a.ReturnBatchId, noRemoval ? null : id), token);
                string location = noRemoval ? worker.Pawn.Registry.PawnReferenceToken : parcelId.ToString(CultureInfo.InvariantCulture);
                string? assignment = noRemoval ? EmptyToNull(worker.Pawn.Registry.AssignmentId) : worker.AssignmentId;
                await db.PawnCustody.Where(p => p.Id == worker.PawnCustodyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, noRemoval ? "Home" : "ReturnPending")
                    .SetProperty(p => p.LocationId, location).SetProperty(p => p.AssignmentId, assignment).SetProperty(p => p.Tombstone, !noRemoval)
                    .SetProperty(p => p.FrontierTransitionId, id).SetProperty(p => p.Revision, p => p.Revision + 1), token);
            }
            if (intent.Kind == OutpostPrepareKind.Found)
            {
                await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "Closed")
                    .SetProperty(o => o.OccupiesSlot, false).SetProperty(o => o.ClosedAt, at).SetProperty(o => o.Revision, revision).SetProperty(o => o.LastTransitionId, id), token);
                await db.OutpostReservations.Where(r => r.OperationId == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Active, false), token);
            }
            else await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Revision, revision).SetProperty(o => o.LastTransitionId, id), token);
            byte[] result = MakeReply(operation, OutpostOperationStatus.Cancelled, id, revision, intent, parcelIds).ToByteArray();
            await db.OutpostOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "Cancelled")
                .SetProperty(o => o.ReservedSilver, 0).SetProperty(o => o.TransitionId, id).SetProperty(o => o.Result, result), token);
    }
}
