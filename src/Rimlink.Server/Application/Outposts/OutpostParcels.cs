using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;
using static Rimlink.Server.Application.Outposts.OutpostAssignmentService;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostReturnBatch(string Id, string TransitionId, OutpostScopeMessage Scope, string OutpostId,
    string ManifestId, ByteString ManifestDigest, long ValuationRevision,
    IReadOnlyList<OutpostWorkerCapture> Workers, IReadOnlyList<ResourceParcel> Resources);

/// <summary>Stage durable own mail inside the caller's transaction; physical import needs a separate durable start.</summary>
public sealed class OutpostParcels(RimlinkDbContext db, OutpostAuthority authority,
    OutpostAssignmentService assignments, MailboxService mailbox, OutpostClock clock)
{
    public async Task<IReadOnlyList<long>> StageHeldAsync(OutpostLease lease, OutpostReturnBatch batch, CancellationToken ct)
    {
        if (!lease.CoversOwner(batch.Scope.OwnerPlayerId) || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("outpost-return-requires-owner-transaction");
        if (!Uuid(batch.Id) || !Uuid(batch.TransitionId) || batch.Workers.Count > 4 || batch.Resources.Count > 2
            || batch.Workers.Select(w => w.PawnCustodyId).Distinct().Count() != batch.Workers.Count
            || batch.Resources.Any(r => r.SchemaVersion != 1 || r.DefName is not ("Steel" or "WoodLog") || r.Count is < 1 or > 1000 || r.ImportValueX100 < 0))
            throw Refuse(ErrorCode.OutpostPayloadInvalid, "return-batch-invalid");
        var children = Split(batch);
        var existing = await db.OutpostParcelLinks.AsNoTracking().Where(p => p.BatchId == batch.Id).OrderBy(p => p.ParcelId).ToListAsync(ct);
        if (existing.Count != 0)
        {
            if (existing.Count != children.Count || existing.Where((link, i) => !link.Payload.AsSpan().SequenceEqual(children[i].Payload.ToByteArray())
                || !link.PayloadDigest.AsSpan().SequenceEqual(SHA256.HashData(link.Payload))).Any())
                throw Refuse(ErrorCode.RequestIntentMismatch, "return-batch-reused");
            return existing.Select(p => p.ParcelId).ToArray();
        }
        var ids = new List<long>();
        foreach (var child in children)
        {
            byte[] payload = child.Payload.ToByteArray();
            var parcel = new Parcel { ToPlayerId = batch.Scope.OwnerPlayerId, Kind = ParcelKindDb.LotReturn,
                Pawns = child.Pawns.ToByteArray(), SizeBytes = child.Pawns.CalculateSize() + payload.Length,
                CreatedAt = clock.Sample().Utc, ClientRequestId = batch.Id, RequestKind = "outpost-return" };
            db.Parcels.Add(parcel); await db.SaveChangesAsync(ct); ids.Add(parcel.Id);
            decimal value = child.Pawns.Pawns.Sum(w => (decimal)w.MarketValueX100) + child.Payload.Resources.Sum(r => (decimal)r.ImportValueX100);
            db.OutpostParcelLinks.Add(new OutpostParcelLink { ParcelId = parcel.Id, WorldId = batch.Scope.WorldInstanceId,
                PlayerId = batch.Scope.OwnerPlayerId, SaveGeneration = batch.Scope.SaveGeneration, RestoreBoundary = batch.Scope.RestoreBoundary,
                BatchId = batch.Id, TransitionId = batch.TransitionId, Payload = payload, PayloadDigest = SHA256.HashData(payload),
                ValueX100 = (long)Math.Clamp(value, 0, long.MaxValue) });
        }
        await db.SaveChangesAsync(ct); return ids;
    }

    public async Task<OutpostDeliveryReply> BeginDeliveryAsync(long playerId, OutpostDeliveryRequest request, CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > Limits.MaxMessageBytes) throw Refuse(ErrorCode.OutpostPayloadInvalid, "delivery-size");
        request = request.Clone();
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await assignments.ScopeAsync(lease, playerId, request.Scope, ct);
        if (!Uuid(request.ClientRequestId) || request.PayloadDigest.Length != 32 || request.ParcelId <= 0)
            throw Refuse(ErrorCode.OutpostPayloadInvalid, "delivery-identity");
        byte[] digest = SHA256.HashData(request.ToByteArray());
        var link = await db.OutpostParcelLinks.AsNoTracking().SingleOrDefaultAsync(p => p.ParcelId == request.ParcelId && p.PlayerId == playerId, ct);
        if (link is null || link.WorldId != request.Scope.WorldInstanceId || link.SaveGeneration != request.Scope.SaveGeneration
            || link.RestoreBoundary != request.Scope.RestoreBoundary || !link.PayloadDigest.AsSpan().SequenceEqual(request.PayloadDigest.Span))
            throw Refuse(ErrorCode.OutpostConflict, "delivery-payload-mismatch");
        var repeated = await db.OutpostDeliveryAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.PlayerId == playerId && a.WorldId == request.Scope.WorldInstanceId
            && a.SaveGeneration == request.Scope.SaveGeneration && a.RestoreBoundary == request.Scope.RestoreBoundary && a.RequestId == request.ClientRequestId, ct);
        if (repeated is not null)
        {
            if (!repeated.IntentDigest.AsSpan().SequenceEqual(digest)) throw Refuse(ErrorCode.RequestIntentMismatch, "delivery-intent-reused");
            return Reply(repeated, request);
        }
        if (link.AcceptedSnapshotId is not null) throw Refuse(ErrorCode.OutpostConflict, "delivery-already-received");
        var parcel = await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == link.ParcelId, ct);
        if (parcel.DeliveredAt is not null || parcel.Decision is ParcelDecisionDb.Awaiting or ParcelDecisionDb.Refused)
            throw Refuse(ErrorCode.OutpostConflict, "delivery-parcel-not-open");
        await assignments.ProofAsync(request.Scope, request.CanonicalSnapshot, true, ct);
        if (await db.ArchivedColonies.AnyAsync(a => a.PlayerId == playerId && a.SaveGeneration == request.Scope.SaveGeneration && a.State == ArchiveState.Archived, ct)
            || await db.OutpostDeliveryAttempts.AnyAsync(a => a.ParcelId == request.ParcelId && a.Active, ct))
            throw Refuse(ErrorCode.RecoveryRequired, "delivery-lifecycle-conflict");
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        string id = Guid.NewGuid().ToString("D");
        var attempt = new OutpostDeliveryAttempt { Id = id, WorldId = request.Scope.WorldInstanceId, PlayerId = playerId,
            SaveGeneration = request.Scope.SaveGeneration, SaveWriteEpoch = request.Scope.SaveWriteEpoch, RestoreBoundary = request.Scope.RestoreBoundary,
            RequestId = request.ClientRequestId, IntentDigest = digest, PayloadDigest = link.PayloadDigest, ParcelId = link.ParcelId,
            CanonicalSnapshotId = request.CanonicalSnapshot.SnapshotId, CanonicalSnapshotHash = request.CanonicalSnapshot.RawSha256.ToByteArray(),
            State = "Started", Active = true, TransitionId = id, CreatedAt = clock.Sample().Utc };
        await authority.CommitHeldAsync(lease, new(id, attempt.WorldId, playerId, attempt.SaveGeneration, attempt.RestoreBoundary,
            "BeginDelivery", "player:" + playerId.ToString(CultureInfo.InvariantCulture), new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFrom(request.ToByteArray())),
            (_, _) => { db.OutpostDeliveryAttempts.Add(attempt); return Task.CompletedTask; }, ct);
        return Reply(attempt, request);
    }

    internal async Task AnnounceAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        foreach (long id in ids) await mailbox.Announce(await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == id, ct));
    }
    private static OutpostDeliveryReply Reply(OutpostDeliveryAttempt attempt, OutpostDeliveryRequest request) =>
        new() { AttemptId = attempt.Id, Authorization = request.Clone(), AuthoritativeRevision = 1, AckOnly = attempt.State == "Accepted" };

    private sealed record Child(PawnList Pawns, OutpostParcelPayload Payload);
    private static List<Child> Split(OutpostReturnBatch batch)
    {
        OutpostParcelPayload Payload() => new() { SchemaVersion = 1, Scope = batch.Scope.Clone(), OutpostId = batch.OutpostId,
            BatchOperationId = batch.Id, ManifestId = batch.ManifestId, ManifestDigest = batch.ManifestDigest, ValuationRevision = batch.ValuationRevision };
        var children = new List<Child>(); var current = new Child(new(), Payload()); current.Payload.Resources.Add(batch.Resources.Select(r => r.Clone()));
        foreach (var worker in batch.Workers.OrderBy(w => w.PawnCustodyId, StringComparer.Ordinal))
        {
            if (worker.Pawn is null || worker.Pawn.ScribeXmlZstd.Length == 0 || worker.Pawn.MarketValueX100 < 0 || !Uuid(worker.PawnCustodyId) || !Uuid(worker.AssignmentId)
                || worker.Pawn.Registry?.PawnCustodyId != worker.PawnCustodyId || !SHA256.HashData(worker.Pawn.ToByteArray()).AsSpan().SequenceEqual(worker.BlobSha256.Span))
                throw Refuse(ErrorCode.OutpostPayloadInvalid, "return-worker-invalid");
            var candidate = new Child(current.Pawns.Clone(), current.Payload.Clone());
            Add(candidate, worker);
            if (candidate.Pawns.CalculateSize() + candidate.Payload.CalculateSize() + 256 > Limits.EconomyMaxParcelBytes)
            {
                if (current.Pawns.Pawns.Count != 0 || current.Payload.Resources.Count != 0) children.Add(current);
                current = new(new(), Payload()); Add(current, worker);
                if (current.Pawns.CalculateSize() + current.Payload.CalculateSize() + 256 > Limits.EconomyMaxParcelBytes)
                    throw Refuse(ErrorCode.OutpostPayloadInvalid, "return-worker-too-large");
            }
            else current = candidate;
        }
        if (current.Pawns.Pawns.Count != 0 || current.Payload.Resources.Count != 0) children.Add(current);
        for (int i = 0; i < children.Count; i++)
        {
            var payload = children[i].Payload; payload.ChildIndex = i; payload.ChildCount = children.Count;
            payload.Kind = payload.PawnCustodyIds.Count == 0 ? OutpostPayloadKind.Resources : payload.Resources.Count == 0 ? OutpostPayloadKind.Workers : OutpostPayloadKind.ReturnBatch;
        }
        return children;
    }
    private static void Add(Child child, OutpostWorkerCapture worker)
    {
        var pawn = worker.Pawn.Clone(); pawn.Registry.AssignmentId = worker.AssignmentId; pawn.Registry.Tombstone = false;
        child.Pawns.Pawns.Add(pawn); child.Payload.PawnCustodyIds.Add(worker.PawnCustodyId); child.Payload.AssignmentIds.Add(worker.AssignmentId);
    }
}
