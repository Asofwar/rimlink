using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using static Rimlink.Server.Application.Outposts.OutpostAssignmentService;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Owner-only settlement and mutations, serialized with admission, snapshots and lifecycle boundaries.</summary>
public sealed class OutpostService(RimlinkDbContext db, OutpostAuthority authority,
    OutpostAssignmentService assignments, OutpostManifestService manifests, OutpostParcels parcels, OutpostClock clock)
{
    public async Task<OutpostStateReply> StateAsync(long playerId, OutpostStateQuery request, CancellationToken ct)
    {
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await assignments.ScopeAsync(lease, playerId, request.Scope, ct);
        var now = clock.Sample();
        var rows = await db.Outposts.AsNoTracking().Where(o => o.PlayerId == playerId && o.WorldId == request.Scope.WorldInstanceId
            && o.SaveGeneration == request.Scope.SaveGeneration && o.RestoreBoundary == request.Scope.RestoreBoundary).OrderBy(o => o.Id).ToListAsync(ct);
        foreach (var row in rows.Where(o => o.State == "Active" && OutpostClock.Elapsed(new(o.CheckpointUtc, o.ClockProcessId, o.ClockTimestamp, o.ClockFrequency), now) > TimeSpan.Zero))
        {
            string id = Guid.NewGuid().ToString("D"); var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
            await authority.CommitHeldAsync(lease, new(id, row.WorldId, playerId, row.SaveGeneration, row.RestoreBoundary,
                "Production", "player:" + playerId.ToString(CultureInfo.InvariantCulture), new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFromUtf8(row.Id), OutpostId: row.Id),
                async (_, token) => { await assignments.SettleAsync(row, now, token); }, ct);
        }
        rows = await db.Outposts.AsNoTracking().Where(o => o.PlayerId == playerId && o.WorldId == request.Scope.WorldInstanceId
            && o.SaveGeneration == request.Scope.SaveGeneration && o.RestoreBoundary == request.Scope.RestoreBoundary).OrderBy(o => o.Id).ToListAsync(ct);
        var reply = new OutpostStateReply { Scope = request.Scope.Clone(), AuthoritativeRevision = rows.Count == 0 ? 0 : rows.Max(o => o.Revision) };
        foreach (var row in rows)
        {
            var workers = await db.OutpostAssignments.AsNoTracking().Where(a => a.OutpostId == row.Id && a.Active).OrderBy(a => a.PawnCustodyId).ToListAsync(ct);
            reply.Outposts.Add(new OutpostSummary { OutpostId = row.Id, Profile = (OutpostProfile)row.Profile, SurfaceId = "planet", Tile = row.Tile,
                Revision = row.Revision, Stock = row.Stock, Archived = row.State == "Archived", RecoveryRequired = row.State == "SafetyHold",
                PawnCustodyIds = { workers.Select(w => w.PawnCustodyId) }, AssignmentIds = { workers.Select(w => w.Id) }, HasFraction = row.FractionNumerator != "0" });
        }
        foreach (var operation in await db.OutpostOperations.AsNoTracking().Where(o => o.PlayerId == playerId && o.WorldId == request.Scope.WorldInstanceId
            && o.SaveGeneration == request.Scope.SaveGeneration && o.RestoreBoundary == request.Scope.RestoreBoundary && (o.State == "Prepared" || o.State == "CancelFenced")).ToListAsync(ct))
            reply.PendingOperations.Add(await assignments.ReplyAsync(operation, ct));
        var manifest = await db.OutpostManifests.AsNoTracking().SingleOrDefaultAsync(m => m.Active && m.Confirmed && m.WorldId == request.Scope.WorldInstanceId, ct);
        if (manifest is not null) { reply.ManifestId = manifest.Id; reply.ManifestDigest = ByteString.CopyFrom(manifest.Digest); }
        return reply;
    }

    public async Task<OutpostManifestPage> ReadManifestAsync(long playerId, OutpostManifestReadRequest request, CancellationToken ct)
    {
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await assignments.ScopeAsync(lease, playerId, request.Scope, ct);
        return await manifests.ReadConfirmedHeldAsync(lease, request, ct);
    }

    public async Task<OutpostOperationReply> MutateAsync(long playerId, OutpostMutationRequest request, CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > Limits.MaxMessageBytes) throw Refuse(ErrorCode.OutpostPayloadInvalid, "mutation-size");
        request = request.Clone();
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await assignments.ScopeAsync(lease, playerId, request.Scope, ct);
        if (!Uuid(request.ClientRequestId) || request.IntentDigest.Length != 32 || !Digest(request).Equals(request.IntentDigest))
            throw Refuse(ErrorCode.OutpostPayloadInvalid, "mutation-identity");
        if (await assignments.ExistingAsync(request.Scope, request.ClientRequestId, request.IntentDigest, ct) is { } previous)
        {
            if (previous.Kind != request.Kind.ToString() || !previous.Intent.AsSpan().SequenceEqual(request.ToByteArray()))
                throw Refuse(ErrorCode.RequestIntentMismatch, "mutation-intent-reused");
            return await assignments.ReplyAsync(previous, ct);
        }
        if (request.Kind is not (OutpostMutationKind.Claim or OutpostMutationKind.Recall or OutpostMutationKind.Close or OutpostMutationKind.Switch)
            || !Uuid(request.OutpostId) || request.PawnCustodyIds.Count > 4 || request.AssignmentIds.Count > 4)
            throw Refuse(ErrorCode.OutpostPayloadInvalid, "mutation-shape");
        var row = await db.Outposts.AsNoTracking().SingleOrDefaultAsync(o => o.Id == request.OutpostId, ct);
        if (row is null || row.PlayerId != playerId || row.WorldId != request.Scope.WorldInstanceId || row.SaveGeneration != request.Scope.SaveGeneration
            || row.RestoreBoundary != request.Scope.RestoreBoundary || row.State is not ("Active" or "SafetyHold") || row.Revision != request.ExpectedRevision
            || row.Tile != request.Tile || request.SurfaceId != "planet" || row.ManifestId != request.ManifestId
            || !row.ManifestDigest.AsSpan().SequenceEqual(request.ManifestDigest.Span)
            || request.Kind != OutpostMutationKind.Switch && row.Profile != (int)request.Profile)
            throw Refuse(ErrorCode.OutpostConflict, "mutation-outpost-changed");
        await assignments.ProofAsync(request.Scope, request.CanonicalSnapshot, true, ct);
        var roster = await (from a in db.OutpostAssignments.AsNoTracking() join o in db.OutpostOperations.AsNoTracking() on a.PreparedOperationId equals o.Id
            where a.OutpostId == row.Id && a.Active && o.State == "Committed" select a).OrderBy(a => a.PawnCustodyId).ToListAsync(ct);
        var selected = request.Kind == OutpostMutationKind.Close ? roster : roster.Where(a => request.PawnCustodyIds.Contains(a.PawnCustodyId)).ToList();
        if (request.Kind == OutpostMutationKind.Recall)
        {
            if (selected.Count == 0 || selected.Count != request.PawnCustodyIds.Count || request.PawnCustodyIds.Distinct().Count() != selected.Count
                || !selected.Select(a => a.Id).Order().SequenceEqual(request.AssignmentIds.Order())) throw Refuse(ErrorCode.OutpostConflict, "recall-worker-changed");
        }
        else if (request.PawnCustodyIds.Count != 0 || request.AssignmentIds.Count != 0) throw Refuse(ErrorCode.OutpostPayloadInvalid, "unexpected-worker-selection");
        bool closes = request.Kind == OutpostMutationKind.Close || request.Kind == OutpostMutationKind.Recall && selected.Count == roster.Count;
        if ((closes || request.Kind == OutpostMutationKind.Switch) && await db.OutpostOperations.AnyAsync(o => o.OutpostId == row.Id && (o.State == "Prepared" || o.State == "CancelFenced"), ct))
            throw Refuse(ErrorCode.RecoveryRequired, "incomplete-staffing-obligation");
        if (request.Kind == OutpostMutationKind.Switch)
        {
            await manifests.RequireActiveHeldAsync(lease, request.SurfaceId, request.Tile, request.Profile, ct);
            if (request.Profile is not (OutpostProfile.Wood or OutpostProfile.Steel) || row.State != "Active") throw Refuse(ErrorCode.OutpostConflict, "switch-profile-invalid");
            foreach (var worker in roster) ValidateWorker(JsonParser.Default.Parse<OutpostWorkerCapture>(worker.FactsJson), request.Profile);
        }
        var manifest = await db.OutpostManifests.AsNoTracking().SingleAsync(m => m.Id == row.ManifestId, ct);
        var document = OutpostManifestDocument.Parser.ParseFrom(manifest.Document);
        string resource = row.Profile == (int)OutpostProfile.Wood ? "WoodLog" : "Steel";
        long unitValue = document.Resources.Single(r => r.DefName == resource).UnitValueX100;
        var now = clock.Sample(); string id = Guid.NewGuid().ToString("D");
        var operation = new OutpostOperation { Id = id, WorldId = row.WorldId, PlayerId = playerId, SaveGeneration = row.SaveGeneration,
            SaveWriteEpoch = request.Scope.SaveWriteEpoch, RestoreBoundary = row.RestoreBoundary, RequestId = request.ClientRequestId,
            Kind = request.Kind.ToString(), Intent = request.ToByteArray(), IntentDigest = request.IntentDigest.ToByteArray(), State = "Committed",
            OutpostId = row.Id, TransitionId = id, CreatedAt = now.Utc };
        var reply = new OutpostOperationReply { Scope = request.Scope.Clone(), ClientRequestId = request.ClientRequestId, IntentDigest = request.IntentDigest,
            Status = OutpostOperationStatus.Committed, TransitionId = id, AuthoritativeRevision = row.Revision + 1 };
        IReadOnlyList<long> parcelIds = [];
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        await authority.CommitHeldAsync(lease, new(id, row.WorldId, playerId, row.SaveGeneration, row.RestoreBoundary,
            request.Kind.ToString(), "player:" + playerId.ToString(CultureInfo.InvariantCulture), new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), ByteString.CopyFrom(operation.Intent), OutpostId: row.Id),
            async (_, token) =>
            {
                await assignments.SettleAsync(row, now, token);
                var settled = await db.Outposts.AsNoTracking().SingleAsync(o => o.Id == row.Id, token);
                if (request.Kind == OutpostMutationKind.Switch && settled.Stock != 0) throw Refuse(ErrorCode.OutpostConflict, "switch-stock-not-empty");
                if ((closes || request.Kind == OutpostMutationKind.Switch) && settled.FractionNumerator != "0" && !request.ConfirmFractionDiscard)
                    throw Refuse(ErrorCode.OutpostConflict, "fraction-discard-confirmation-required");
                var resources = new List<ResourceParcel>();
                if ((closes || request.Kind == OutpostMutationKind.Claim) && settled.Stock != 0)
                    resources.Add(new() { SchemaVersion = 1, DefName = resource, Count = settled.Stock, ImportValueX100 = checked(unitValue * settled.Stock) });
                var workers = (closes || request.Kind == OutpostMutationKind.Recall ? selected : []).Select(a => JsonParser.Default.Parse<OutpostWorkerCapture>(a.FactsJson)).ToArray();
                parcelIds = await parcels.StageHeldAsync(lease, new(id, id, request.Scope.Clone(), row.Id, row.ManifestId, ByteString.CopyFrom(row.ManifestDigest), manifest.Revision, workers, resources), token);
                foreach (var worker in workers)
                {
                    var links = await db.OutpostParcelLinks.AsNoTracking().Where(p => p.BatchId == id).ToListAsync(token);
                    var link = links.Single(p => OutpostParcelPayload.Parser.ParseFrom(p.Payload).PawnCustodyIds.Contains(worker.PawnCustodyId));
                    await db.OutpostAssignments.Where(a => a.Id == worker.AssignmentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Active, false)
                        .SetProperty(a => a.EndedAt, now.Utc).SetProperty(a => a.ReturnBatchId, id), token);
                    await db.PawnCustody.Where(p => p.Id == worker.PawnCustodyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "ReturnPending")
                        .SetProperty(p => p.LocationId, link.ParcelId.ToString(CultureInfo.InvariantCulture)).SetProperty(p => p.Tombstone, true)
                        .SetProperty(p => p.FrontierTransitionId, id).SetProperty(p => p.Revision, p => p.Revision + 1), token);
                }
                if (closes) await db.OutpostReservations.Where(r => r.WorldId == row.WorldId && r.PlayerId == row.PlayerId && r.Tile == row.Tile && r.Active)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Active, false), token);
                await db.Outposts.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Stock, closes || request.Kind == OutpostMutationKind.Claim ? 0 : settled.Stock)
                    .SetProperty(o => o.FractionNumerator, closes || request.Kind == OutpostMutationKind.Switch ? "0" : settled.FractionNumerator)
                    .SetProperty(o => o.FractionDenominator, closes || request.Kind == OutpostMutationKind.Switch ? "1" : settled.FractionDenominator)
                    .SetProperty(o => o.Profile, request.Kind == OutpostMutationKind.Switch ? (int)request.Profile : row.Profile)
                    .SetProperty(o => o.State, closes ? "Closed" : row.State).SetProperty(o => o.OccupiesSlot, !closes)
                    .SetProperty(o => o.ClosedAt, closes ? now.Utc : row.ClosedAt).SetProperty(o => o.Revision, row.Revision + 1).SetProperty(o => o.LastTransitionId, id), token);
                reply.ParcelIds.Add(parcelIds); operation.Result = reply.ToByteArray(); db.OutpostOperations.Add(operation);
            }, ct);
        await parcels.AnnounceAsync(parcelIds, ct); return await assignments.ReplyAsync(operation, ct);
    }

    internal static ByteString Digest(OutpostMutationRequest request)
    { var copy = request.Clone(); copy.IntentDigest = ByteString.Empty; return ByteString.CopyFrom(SHA256.HashData(copy.ToByteArray())); }
}
