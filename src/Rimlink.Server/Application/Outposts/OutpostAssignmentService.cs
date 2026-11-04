using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Parcel = Rimlink.Server.Storage.Entities.Parcel;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Admission obligations: no local return is inferred from a timeout or a missing receipt.</summary>
public sealed class OutpostAssignmentService(RimlinkDbContext db, OutpostAuthority authority,
    OutpostSnapshotGuard snapshots, OutpostManifestService manifests, OutpostTileClaims tiles,
    WalletService wallet, EconomyGate gate, EconomyGuard economy, OutpostClock clock)
{
    public async Task<OutpostOperationReply> PrepareAsync(long playerId, OutpostPrepareRequest request, CancellationToken ct)
    {
        if (request is null || request.CalculateSize() > Limits.EconomyMaxParcelBytes) throw Refuse(ErrorCode.OutpostPayloadInvalid, "admission-size");
        request = request.Clone();
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await ScopeAsync(lease, playerId, request.Scope, ct);
        ValidateIdentity(request.ClientRequestId, request.IntentDigest);
        if (!request.IntentDigest.Equals(OutpostRequestIdentity.Digest(request))) throw Refuse(ErrorCode.RequestIntentMismatch, "admission-digest");
        if (await ExistingAsync(request.Scope, request.ClientRequestId, request.IntentDigest, ct) is { } repeated)
        {
            if (repeated.Kind != request.Kind.ToString() || !repeated.Intent.AsSpan().SequenceEqual(request.ToByteArray()))
                throw Refuse(ErrorCode.RequestIntentMismatch, "admission-intent-reused");
            return await ReplyAsync(repeated, ct);
        }
        if (request.Kind is not (OutpostPrepareKind.Found or OutpostPrepareKind.Assign) || !Uuid(request.OutpostId)
            || request.Workers.Count is < 1 or > 4 || request.Workers.Select(w => w.PawnCustodyId).Distinct().Count() != request.Workers.Count
            || request.Workers.Select(w => w.AssignmentId).Distinct().Count() != request.Workers.Count)
            throw Refuse(ErrorCode.OutpostPayloadInvalid, "admission-shape");
        gate.RequireOpen(); await gate.RequireNotFrozen(playerId); await economy.RequireNoDebt(playerId);
        if (request.Kind == OutpostPrepareKind.Found) await economy.RequireNoThreat(playerId);
        await ManifestAsync(lease, request, ct);
        var pre = await ProofAsync(request.Scope, request.PreRemoval, true, ct);
        ValidateCaravan(request, pre);
        foreach (var worker in request.Workers)
        {
            ValidateWorker(worker, request.Profile);
            if (!pre.WorkerCaptures.Any(w => w.Equals(worker))) throw Refuse(ErrorCode.OutpostConflict, "worker-not-in-exact-pre-snapshot");
            var registry = pre.Registry.SingleOrDefault(e => e.PawnCustodyId == worker.PawnCustodyId && !e.NonAuthoritativeMirror);
            var custody = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == worker.PawnCustodyId, ct);
            if (registry is null || registry.Tombstone || custody is null || custody.LocationKind != "Home" || custody.Tombstone
                || custody.PlayerId != playerId || custody.WorldId != request.Scope.WorldInstanceId || !worker.Pawn.Registry.Equals(registry)
                || await db.OutpostAssignments.AnyAsync(a => a.PawnCustodyId == worker.PawnCustodyId && a.Active || a.Id == worker.AssignmentId, ct))
                throw Refuse(ErrorCode.OutpostConflict, "worker-custody-unavailable");
        }
        OutpostRow? existing = await db.Outposts.AsNoTracking().SingleOrDefaultAsync(o => o.Id == request.OutpostId, ct);
        if (request.Kind == OutpostPrepareKind.Found)
        {
            if (existing is not null || request.ExpectedRevision != 0 || await db.Outposts.AnyAsync(o => o.PlayerId == playerId && o.OccupiesSlot, ct)
                || await db.OutpostReservations.AnyAsync(o => o.PlayerId == playerId && o.Active, ct)) throw Refuse(ErrorCode.OutpostConflict, "outpost-slot-unavailable");
            await tiles.RequireAvailableHeldAsync(lease, request.SurfaceId, request.Tile, null, ct);
        }
        else if (existing is null || existing.PlayerId != playerId || existing.WorldId != request.Scope.WorldInstanceId
            || existing.SaveGeneration != request.Scope.SaveGeneration || existing.RestoreBoundary != request.Scope.RestoreBoundary
            || existing.State != "Active" || existing.Revision != request.ExpectedRevision || existing.Tile != request.Tile
            || existing.Profile != (int)request.Profile || !existing.OccupiesSlot
            || await db.OutpostAssignments.CountAsync(a => a.OutpostId == request.OutpostId && a.Active, ct) + request.Workers.Count > 4)
            throw Refuse(ErrorCode.OutpostConflict, "outpost-staffing-unavailable");
        string id = Guid.NewGuid().ToString("D"); var now = clock.Sample();
        var operation = new OutpostOperation { Id = id, WorldId = request.Scope.WorldInstanceId, PlayerId = playerId,
            SaveGeneration = request.Scope.SaveGeneration, SaveWriteEpoch = request.Scope.SaveWriteEpoch, RestoreBoundary = request.Scope.RestoreBoundary,
            RequestId = request.ClientRequestId, Kind = request.Kind.ToString(), Intent = request.ToByteArray(), IntentDigest = request.IntentDigest.ToByteArray(),
            State = "Prepared", OutpostId = request.OutpostId, TransitionId = id, ReservedSilver = request.Kind == OutpostPrepareKind.Found ? 500 : 0, CreatedAt = now.Utc };
        long revision = (existing?.Revision ?? 0) + 1;
        operation.Result = MakeReply(operation, OutpostOperationStatus.Prepared, id, revision, request).ToByteArray();
        await TransitionAsync(lease, operation, id, "Prepare", async token =>
        {
            // Recheck available silver after beginning the SQLite write transaction: every legacy debit also writes here.
            if (operation.ReservedSilver > await wallet.Available(playerId)) throw Refuse(ErrorCode.WalletInsufficient, "founding-reserve-unavailable");
            db.OutpostOperations.Add(operation);
            if (existing is null)
            {
                db.Outposts.Add(new OutpostRow { Id = request.OutpostId, WorldId = operation.WorldId, PlayerId = playerId,
                    SaveGeneration = operation.SaveGeneration, RestoreBoundary = operation.RestoreBoundary, Tile = request.Tile, Profile = (int)request.Profile,
                    OccupiesSlot = true, Revision = revision, State = "Prepared", ManifestId = request.ManifestId,
                    ManifestDigest = request.ManifestDigest.ToByteArray(), CreatedAt = now.Utc, LastTransitionId = id,
                    CheckpointUtc = now.Utc, ClockProcessId = now.ProcessId, ClockFrequency = now.Frequency, ClockTimestamp = now.Timestamp });
                db.OutpostReservations.Add(new OutpostReservation { Id = Guid.NewGuid().ToString("D"), WorldId = operation.WorldId,
                    PlayerId = playerId, SaveGeneration = operation.SaveGeneration, Tile = request.Tile, Active = true, OperationId = id });
            }
            else await db.Outposts.Where(o => o.Id == request.OutpostId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Revision, revision)
                .SetProperty(o => o.LastTransitionId, id), token);
            foreach (var worker in request.Workers)
            {
                db.OutpostAssignments.Add(new OutpostAssignment { Id = worker.AssignmentId, OutpostId = request.OutpostId, PawnCustodyId = worker.PawnCustodyId,
                    Active = true, Blob = worker.Pawn.ToByteArray(), BlobDigest = worker.BlobSha256.ToByteArray(), FactsJson = JsonFormatter.Default.Format(worker),
                    PreparedOperationId = id, CreatedAt = now.Utc });
                await db.PawnCustody.Where(p => p.Id == worker.PawnCustodyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "Prepared")
                    .SetProperty(p => p.LocationId, id).SetProperty(p => p.FrontierTransitionId, id).SetProperty(p => p.Revision, p => p.Revision + 1), token);
            }
        }, ct);
        return await ReplyAsync(operation, ct);
    }

    public async Task<OutpostOperationReply> CommitAsync(long playerId, OutpostProofRequest request, CancellationToken ct)
    {
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await ScopeAsync(lease, playerId, request.Scope, ct);
        var operation = await ExistingAsync(request.Scope, request.ClientRequestId, request.IntentDigest, ct) ?? throw Refuse(ErrorCode.OutpostConflict, "operation-missing");
        if (Terminal(operation)) return await ReplyAsync(operation, ct);
        if (operation.State != "Prepared") return await RecoveryAsync(operation, ct);
        var intent = ParseIntent(operation);
        var post = await ProofAsync(request.Scope, request.PostRemoval, true, ct);
        if (!HasReceipt(post, operation.Id, operation, OutpostCancelKind.Unspecified)) throw Refuse(ErrorCode.RecoveryRequired, "exact-removal-receipt-required");
        var outpost = await db.Outposts.AsNoTracking().SingleAsync(o => o.Id == intent.OutpostId, ct);
        if (request.ExpectedRevision != outpost.Revision || outpost.State is not ("Prepared" or "Active")) return await RecoveryAsync(operation, ct);
        try { await ManifestAsync(lease, intent, ct); } catch (RpcException) { return await RecoveryAsync(operation, ct); }
        string id = Guid.NewGuid().ToString("D"); long revision = outpost.Revision + 1; var now = clock.Sample();
        await TransitionAsync(lease, operation, id, "Commit", async token =>
        {
            await SettleAsync(outpost, now, token);
            await db.OutpostOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.ReservedSilver, 0), token);
            if (intent.Kind == OutpostPrepareKind.Found) await wallet.Apply(playerId, -500, "outpost-found", 0);
            foreach (var worker in intent.Workers)
                await db.PawnCustody.Where(p => p.Id == worker.PawnCustodyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "Assigned")
                    .SetProperty(p => p.LocationId, intent.OutpostId).SetProperty(p => p.AssignmentId, worker.AssignmentId)
                    .SetProperty(p => p.Tombstone, true).SetProperty(p => p.FrontierTransitionId, id).SetProperty(p => p.Revision, p => p.Revision + 1), token);
            await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "Active")
                .SetProperty(o => o.Revision, revision).SetProperty(o => o.LastTransitionId, id), token);
            await StoreResultAsync(operation, OutpostOperationStatus.Committed, "Committed", id, revision, [], token);
        }, ct);
        wallet.FlushEvents(); return await LoadReplyAsync(operation.Id, ct);
    }

    public async Task<OutpostOperationReply> CancelAsync(long playerId, OutpostCancelRequest request, CancellationToken ct)
    {
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await ScopeAsync(lease, playerId, request.Scope, ct);
        var operation = await ExistingAsync(request.Scope, request.ClientRequestId, request.IntentDigest, ct) ?? throw Refuse(ErrorCode.OutpostConflict, "operation-missing");
        if (Terminal(operation)) return await ReplyAsync(operation, ct);
        var intent = ParseIntent(operation); var outpost = await db.Outposts.AsNoTracking().SingleAsync(o => o.Id == intent.OutpostId, ct);
        if (request.Kind is not (OutpostCancelKind.AbortNoRemoval or OutpostCancelKind.ReturnAfterRemoval)) throw Refuse(ErrorCode.OutpostPayloadInvalid, "cancellation-kind");
        if (request.Kind == OutpostCancelKind.AbortNoRemoval && request.ContinuousBranchId != intent.Caravan.GameBranchId)
            return await RecoveryAsync(operation, ct);
        OutpostSaveMetadata? proof = null;
        if (request.CancellationProof is not null)
        {
            try { proof = await ProofAsync(request.Scope, request.CancellationProof, true, ct); }
            catch (RpcException) { return await RecoveryAsync(operation, ct); }
        }
        bool removed = proof is not null && HasReceipt(proof, operation.Id, operation, OutpostCancelKind.Unspecified);
        if (request.Kind == OutpostCancelKind.AbortNoRemoval && operation.State == "Prepared")
        {
            if (removed || await db.PawnCustody.AnyAsync(p => p.LocationId == operation.Id && p.LocationKind == "Removed", ct))
                return await RecoveryAsync(operation, ct);
            string fence = Guid.NewGuid().ToString("D");
            await TransitionAsync(lease, operation, fence, "CancelFence", token => StoreResultAsync(operation,
                OutpostOperationStatus.RecoveryRequired, "CancelFenced", fence, outpost.Revision, [], token), ct);
            return await LoadReplyAsync(operation.Id, ct);
        }
        bool noRemoval = request.Kind == OutpostCancelKind.AbortNoRemoval && operation.State == "CancelFenced" && proof is not null
            && HasReceipt(proof, operation.TransitionId!, operation, OutpostCancelKind.AbortNoRemoval);
        if (!noRemoval && !(request.Kind == OutpostCancelKind.ReturnAfterRemoval && removed)) return await RecoveryAsync(operation, ct);
        if (request.ExpectedRevision != outpost.Revision) return await RecoveryAsync(operation, ct);
        string id = Guid.NewGuid().ToString("D"); long revision = outpost.Revision + 1; var now = clock.Sample(); var parcelIds = new List<long>();
        await TransitionAsync(lease, operation, id, noRemoval ? "AbortNoRemoval" : "AbortWithReturn", async token =>
        {
            long parcelId = 0;
            if (!noRemoval)
            {
                var pawns = new PawnList();
                foreach (var worker in intent.Workers)
                {
                    var pawn = worker.Pawn.Clone(); pawn.Registry.AssignmentId = worker.AssignmentId; pawn.Registry.Tombstone = false;
                    pawns.Pawns.Add(pawn);
                }
                var parcel = new Parcel { ToPlayerId = playerId, Kind = ParcelKindDb.LotReturn, Pawns = pawns.ToByteArray(),
                    SizeBytes = pawns.CalculateSize(), CreatedAt = now.Utc, ClientRequestId = request.ClientRequestId, RequestKind = "outpost-return" };
                db.Parcels.Add(parcel); await db.SaveChangesAsync(token); parcelId = parcel.Id; parcelIds.Add(parcelId);
                var payload = new OutpostParcelPayload { SchemaVersion = 1, Kind = OutpostPayloadKind.Workers, Scope = intent.Scope.Clone(),
                    OutpostId = intent.OutpostId, BatchOperationId = id, ManifestId = intent.ManifestId, ManifestDigest = intent.ManifestDigest };
                payload.PawnCustodyIds.Add(intent.Workers.Select(w => w.PawnCustodyId)); payload.AssignmentIds.Add(intent.Workers.Select(w => w.AssignmentId));
                byte[] bytes = payload.ToByteArray();
                db.OutpostParcelLinks.Add(new() { ParcelId = parcelId, WorldId = operation.WorldId, PlayerId = playerId,
                    SaveGeneration = operation.SaveGeneration, RestoreBoundary = operation.RestoreBoundary, BatchId = id,
                    Payload = bytes, PayloadDigest = SHA256.HashData(bytes), TransitionId = id,
                    ValueX100 = intent.Workers.Sum(w => w.Pawn.MarketValueX100) });
            }
            foreach (var worker in intent.Workers)
            {
                await db.OutpostAssignments.Where(a => a.Id == worker.AssignmentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Active, false)
                    .SetProperty(a => a.EndedAt, now.Utc).SetProperty(a => a.ReturnBatchId, noRemoval ? null : id), token);
                string location = noRemoval ? worker.Pawn.Registry.PawnReferenceToken : parcelId.ToString(CultureInfo.InvariantCulture);
                string? assignment = noRemoval ? EmptyToNull(worker.Pawn.Registry.AssignmentId) : worker.AssignmentId;
                await db.PawnCustody.Where(p => p.Id == worker.PawnCustodyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, noRemoval ? "Home" : "ReturnPending")
                    .SetProperty(p => p.LocationId, location).SetProperty(p => p.AssignmentId, assignment).SetProperty(p => p.Tombstone, !noRemoval)
                    .SetProperty(p => p.FrontierTransitionId, id).SetProperty(p => p.Revision, p => p.Revision + 1), token);
            }
            if (intent.Kind == OutpostPrepareKind.Found)
            {
                await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "Closed")
                    .SetProperty(o => o.OccupiesSlot, false).SetProperty(o => o.ClosedAt, now.Utc).SetProperty(o => o.Revision, revision).SetProperty(o => o.LastTransitionId, id), token);
                await db.OutpostReservations.Where(r => r.OperationId == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Active, false), token);
            }
            else await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Revision, revision).SetProperty(o => o.LastTransitionId, id), token);
            await StoreResultAsync(operation, OutpostOperationStatus.Cancelled, "Cancelled", id, revision, parcelIds, token);
        }, ct);
        return await LoadReplyAsync(operation.Id, ct);
    }

    public async Task<OutpostOperationReply> OperationAsync(long playerId, OutpostOperationQuery request, CancellationToken ct)
    {
        await using var lease = await authority.AcquireAsync([playerId], ct); await ScopeAsync(lease, playerId, request.Scope, ct);
        var operation = await ExistingAsync(request.Scope, request.ClientRequestId, request.IntentDigest, ct) ?? throw Refuse(ErrorCode.OutpostConflict, "operation-missing");
        return await ReplyAsync(operation, ct);
    }

    private async Task ScopeAsync(OutpostLease lease, long playerId, OutpostScopeMessage scope, CancellationToken ct)
    {
        if ((await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready) throw Refuse(ErrorCode.OutpostUnavailable, "authority-not-ready");
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct); var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId, ct);
        if (scope is null || player is null || scope.OwnerPlayerId != playerId || scope.WorldInstanceId != head.WorldId
            || scope.SaveGeneration != player.SaveGeneration || scope.SaveWriteEpoch != player.SaveWriteEpoch || scope.RestoreBoundary != head.RestoreBoundary)
            throw Refuse(ErrorCode.OutpostRestoreBoundaryMismatch, "operation-scope-mismatch");
    }
    private async Task<OutpostOperation?> ExistingAsync(OutpostScopeMessage scope, string requestId, ByteString digest, CancellationToken ct)
    {
        ValidateIdentity(requestId, digest);
        var operation = await db.OutpostOperations.AsNoTracking().SingleOrDefaultAsync(o => o.WorldId == scope.WorldInstanceId && o.PlayerId == scope.OwnerPlayerId
            && o.SaveGeneration == scope.SaveGeneration && o.RestoreBoundary == scope.RestoreBoundary && o.RequestId == requestId, ct);
        if (operation is not null && !operation.IntentDigest.AsSpan().SequenceEqual(digest.Span)) throw Refuse(ErrorCode.RequestIntentMismatch, "operation-intent-mismatch");
        return operation;
    }
    private async Task ManifestAsync(OutpostLease lease, OutpostPrepareRequest request, CancellationToken ct)
    {
        await manifests.RequireActiveHeldAsync(lease, request.SurfaceId, request.Tile, request.Profile, ct);
        if (!await db.OutpostManifests.AnyAsync(m => m.Active && m.Confirmed && m.Id == request.ManifestId && m.Digest == request.ManifestDigest.ToByteArray(), ct))
            throw Refuse(ErrorCode.OutpostManifestMismatch, "admission-manifest-changed");
    }
    private async Task<OutpostSaveMetadata> ProofAsync(OutpostScopeMessage scope, OutpostSnapshotProof proof, bool current, CancellationToken ct)
    {
        if (proof is null || !scope.Equals(proof.Scope) || proof.RawSha256.Length != 32) throw Refuse(ErrorCode.RecoveryRequired, "exact-snapshot-required");
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == scope.WorldInstanceId && l.PlayerId == scope.OwnerPlayerId
            && l.SaveGeneration == scope.SaveGeneration && l.SaveWriteEpoch == scope.SaveWriteEpoch && l.RestoreBoundary == scope.RestoreBoundary && l.SnapshotId == proof.SnapshotId, ct);
        if (link is null || !link.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span)) throw Refuse(ErrorCode.RecoveryRequired, "exact-snapshot-mismatch");
        if (current)
        {
            var latest = await db.SaveVersions.AsNoTracking().Where(v => v.PlayerId == scope.OwnerPlayerId && v.SaveGeneration == scope.SaveGeneration)
                .OrderByDescending(v => v.Number).Select(v => v.SnapshotId).FirstOrDefaultAsync(ct);
            if (latest != proof.SnapshotId) throw Refuse(ErrorCode.RecoveryRequired, "canonical-snapshot-changed");
        }
        return OutpostSaveMetadata.Parser.ParseFrom(link.Metadata);
    }
    private static void ValidateCaravan(OutpostPrepareRequest request, OutpostSaveMetadata pre)
    {
        var caravan = request.Caravan;
        var selected = request.Workers.Select(w => w.Pawn.Registry?.PawnReferenceToken ?? "").ToArray();
        if (caravan is null || caravan.CaravanId.Length is < 1 or > 256 || !Uuid(caravan.GameBranchId) || caravan.SurfaceId != request.SurfaceId || caravan.Tile != request.Tile
            || !caravan.ManifestDigest.Equals(request.ManifestDigest) || caravan.ClientRequestId != request.ClientRequestId || !caravan.SelectedCargoEmpty
            || !pre.Caravans.Any(c => c.Equals(caravan)) || !caravan.SelectedCustodyIds.Order().SequenceEqual(request.Workers.Select(w => w.PawnCustodyId).Order())
            || caravan.RosterReferenceTokens.Count > 1024 || caravan.RosterReferenceTokens.Distinct().Count() != caravan.RosterReferenceTokens.Count
            || caravan.RemainingReferenceTokens.Distinct().Count() != caravan.RemainingReferenceTokens.Count || selected.Distinct().Count() != selected.Length
            || selected.Any(s => !caravan.RosterReferenceTokens.Contains(s))
            || !caravan.RosterReferenceTokens.Except(selected).Order().SequenceEqual(caravan.RemainingReferenceTokens.Order())
            || caravan.RemainingReferenceTokens.Count > 0 && !caravan.RemainingMobile)
            throw Refuse(ErrorCode.OutpostConflict, "exact-caravan-attestation-required");
    }
    internal static void ValidateWorker(OutpostWorkerCapture worker, OutpostProfile profile)
    {
        if (!Uuid(worker.PawnCustodyId) || !Uuid(worker.AssignmentId) || worker.Pawn is null || worker.Pawn.Registry is null
            || worker.Pawn.Registry.PawnCustodyId != worker.PawnCustodyId || worker.Pawn.Registry.NonAuthoritativeMirror || worker.Pawn.Registry.Tombstone
            || worker.Pawn.ScribeXmlZstd.Length is < 1 or > Limits.EconomyMaxParcelBytes || worker.Pawn.MarketValueX100 < 0 || worker.BlobSha256.Length != 32
            || !SHA256.HashData(worker.Pawn.ToByteArray()).AsSpan().SequenceEqual(worker.BlobSha256.Span)) throw Refuse(ErrorCode.OutpostPayloadInvalid, "worker-payload-invalid");
        if (!worker.AdultLifeStage || !worker.FreeColonist || !worker.PlayerOwned || !worker.BaselineHuman || !worker.Pawn.Humanlike || worker.Downed || worker.MentalState
            || !float.IsFinite(worker.Consciousness) || !float.IsFinite(worker.Manipulation) || !float.IsFinite(worker.Moving)
            || Math.Min(worker.Consciousness, Math.Min(worker.Manipulation, worker.Moving)) <= 0
            || profile == OutpostProfile.Wood && !worker.PlantsCapable || profile == OutpostProfile.Steel && !worker.MiningCapable)
            throw Refuse(ErrorCode.OutpostIneligible, "worker-ineligible");
    }
    private async Task SettleAsync(OutpostRow outpost, ClockSample now, CancellationToken ct)
    {
        var workers = await (from a in db.OutpostAssignments.AsNoTracking() join o in db.OutpostOperations.AsNoTracking() on a.PreparedOperationId equals o.Id
            where a.OutpostId == outpost.Id && a.Active && o.State == "Committed" select a.FactsJson).ToListAsync(ct);
        var rates = workers.Select(json => JsonParser.Default.Parse<OutpostWorkerCapture>(json)).Select(w => new WorkerRate((OutpostProfile)outpost.Profile,
            outpost.Profile == (int)OutpostProfile.Wood ? w.Plants : w.Mining, Math.Min(w.Consciousness, Math.Min(w.Manipulation, w.Moving)))).ToArray();
        var checkpoint = new ProductionCheckpoint(outpost.CheckpointUtc, outpost.ClockProcessId, outpost.ClockTimestamp, outpost.ClockFrequency);
        var settled = OutpostProduction.Settle(new(outpost.Stock, outpost.FractionNumerator, outpost.FractionDenominator), rates,
            outpost.State == "Active" ? OutpostClock.Elapsed(checkpoint, now) : TimeSpan.Zero).State;
        var next = OutpostClock.Advance(checkpoint, now);
        await db.Outposts.Where(o => o.Id == outpost.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Stock, settled.Stock)
            .SetProperty(o => o.FractionNumerator, settled.FractionNumerator).SetProperty(o => o.FractionDenominator, settled.FractionDenominator)
            .SetProperty(o => o.CheckpointUtc, next.Utc).SetProperty(o => o.ClockProcessId, next.ProcessId)
            .SetProperty(o => o.ClockTimestamp, next.Timestamp).SetProperty(o => o.ClockFrequency, next.Frequency), ct);
    }
    private async Task TransitionAsync(OutpostLease lease, OutpostOperation operation, string id, string kind, Func<CancellationToken, Task> stage, CancellationToken ct)
    {
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        try { await authority.CommitHeldAsync(lease, new(id, operation.WorldId, operation.PlayerId, operation.SaveGeneration, operation.RestoreBoundary,
            kind, "player:" + operation.PlayerId.ToString(CultureInfo.InvariantCulture), new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)),
            ByteString.CopyFrom(operation.Intent), OutpostId: operation.OutpostId), (_, token) => stage(token), ct); }
        catch { wallet.DiscardEvents(); throw; }
    }
    private Task StoreResultAsync(OutpostOperation operation, OutpostOperationStatus status, string state, string id, long revision, IEnumerable<long> parcels, CancellationToken ct)
    {
        byte[] result = MakeReply(operation, status, id, revision, ParseIntent(operation), parcels).ToByteArray();
        return db.OutpostOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.State, state)
            .SetProperty(o => o.ReservedSilver, state is "Prepared" or "CancelFenced" ? operation.ReservedSilver : 0)
            .SetProperty(o => o.TransitionId, id).SetProperty(o => o.Result, result), ct);
    }
    private async Task<OutpostOperationReply> LoadReplyAsync(string id, CancellationToken ct) => await ReplyAsync(await db.OutpostOperations.AsNoTracking().SingleAsync(o => o.Id == id, ct), ct);
    private async Task<OutpostOperationReply> ReplyAsync(OutpostOperation operation, CancellationToken ct)
    {
        var reply = OutpostOperationReply.Parser.ParseFrom(operation.Result);
        reply.TransitionDigest = ByteString.CopyFrom(await db.OutpostTransitions.AsNoTracking().Where(t => t.Id == reply.TransitionId).Select(t => t.Digest).SingleAsync(ct));
        return reply;
    }
    private async Task<OutpostOperationReply> RecoveryAsync(OutpostOperation operation, CancellationToken ct)
    { var reply = await ReplyAsync(operation, ct); reply.Status = OutpostOperationStatus.RecoveryRequired; return reply; }
    private static OutpostOperationReply MakeReply(OutpostOperation operation, OutpostOperationStatus status, string id, long revision,
        OutpostPrepareRequest intent, IEnumerable<long>? parcels = null)
    {
        var result = new OutpostOperationReply { Scope = intent.Scope.Clone(), ClientRequestId = operation.RequestId, Status = status,
            IntentDigest = intent.IntentDigest, TransitionId = id, AuthoritativeRevision = revision, PreparedIntent = intent.Clone() };
        if (parcels is not null) result.ParcelIds.Add(parcels); return result;
    }
    internal static OutpostPrepareRequest ParseIntent(OutpostOperation operation) => OutpostPrepareRequest.Parser.ParseFrom(operation.Intent);
    internal static bool HasReceipt(OutpostSaveMetadata metadata, string transition, OutpostOperation operation, OutpostCancelKind kind) =>
        metadata.Receipts.Any(r => r.TransitionId == transition && r.ClientRequestId == operation.RequestId && r.IntentDigest.Span.SequenceEqual(operation.IntentDigest) && r.CancellationKind == kind);
    private static bool Terminal(OutpostOperation operation) => operation.State is "Committed" or "Cancelled";
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    internal static bool Uuid(string value) => (Guid.TryParseExact(value, "D", out var d) && d.ToString("D") == value) || (Guid.TryParseExact(value, "N", out var n) && n.ToString("N") == value);
    private static void ValidateIdentity(string id, ByteString digest) { if (!Uuid(id) || digest.Length != 32) throw Refuse(ErrorCode.OutpostPayloadInvalid, "operation-identity"); }
    internal static RpcException Refuse(ErrorCode code, string reason) => RpcErrors.With(StatusCode.FailedPrecondition, code, RefusalKeys.OutpostsOperation, reason);
}
