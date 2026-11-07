using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using static Rimlink.Server.Application.Outposts.OutpostAssignmentService;

namespace Rimlink.Server.Application.Outposts;

/// <summary>Authorizes one local ordinary return only after fencing the exact original operation.</summary>
public sealed partial class OrdinaryReturnService(RimlinkDbContext db, OutpostAuthority authority, OutpostAssignmentService assignments)
{
    public Task<OrdinaryOperationReply> ResolveAsync(long playerId, OrdinaryOperationQuery query, CancellationToken ct) =>
        ResolveCoreAsync(playerId, query?.Operation, false, ct);

    public Task<OrdinaryOperationReply> BeginReturnAsync(long playerId, OrdinaryCancelRequest request, CancellationToken ct) =>
        ResolveCoreAsync(playerId, request, true, ct);

    private async Task<OrdinaryOperationReply> ResolveCoreAsync(long playerId, OrdinaryCancelRequest? input, bool begin, CancellationToken ct)
    {
        if (input is null || input.CalculateSize() > Limits.MaxMessageBytes) throw Refuse(ErrorCode.OutpostPayloadInvalid, "ordinary-return-size");
        var request = input.Clone();
        var payload = Payload(request);
        await using var lease = await authority.AcquireAsync([playerId], ct);
        await assignments.ScopeAsync(lease, playerId, request.Scope, ct); // Scope precedes all replay.
        await assignments.ProofAsync(request.Scope, request.RemovalSnapshot, false, ct);
        await assignments.ProofAsync(request.Scope, request.CanonicalSnapshot, false, ct);
        var capture = request.Capture;
        var repeated = await db.OutpostOrdinaryReturns.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId
            && r.RequestKind == (int)capture.RequestKind && r.RequestId == capture.ClientRequestId, ct);
        if (repeated is not null)
        {
            if (!repeated.Intent.AsSpan().SequenceEqual(request.ToByteArray())) throw Refuse(ErrorCode.RequestIntentMismatch, "ordinary-return-intent-reused");
            return await ReplyAsync(repeated, request, ct);
        }
        var positive = await SourceAsync(db, request, ct);
        if (positive is not null) return positive;
        await ValidateNegativeAsync(db, request, ct);
        if (!begin) return new() { Status = OrdinaryOperationStatus.Unknown };
        var head = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        string id = request.ReturnAttemptId;
        var record = CreateRecord(request, payload);
        await authority.CommitHeldAsync(lease, new(id, record.WorldId, playerId, record.SaveGeneration, record.RestoreBoundary,
            "OrdinaryReturnStarted", "player:" + playerId.ToString(CultureInfo.InvariantCulture),
            new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), request.ToByteString()), async (_, token) =>
                await StageReturnCoreAsync(db, record, request, token), ct);
        return await ReplyAsync(record, request, ct);
    }

    internal static async Task ValidateNegativeAsync(RimlinkDbContext db, OrdinaryCancelRequest request, CancellationToken ct)
    {
        long playerId = request.Scope.OwnerPlayerId; var capture = request.Capture;
        // An absent source is not a return grant: the following branch and custody checks are mandatory.
        var canonical = await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PlayerId == playerId && v.IsCurrent, ct);
        if (canonical?.SnapshotId != request.CanonicalSnapshot.SnapshotId || canonical.SaveGeneration != request.Scope.SaveGeneration
            || canonical.SaveWriteEpoch != request.Scope.SaveWriteEpoch)
            throw Refuse(ErrorCode.RecoveryRequired, "ordinary-canonical-snapshot-changed");
        var removed = await db.SaveVersions.AsNoTracking().SingleOrDefaultAsync(v => v.PlayerId == playerId
            && v.SnapshotId == request.RemovalSnapshot.SnapshotId, ct);
        RequireRemoval(SaveStore.RecoveryOf(removed), request);
        var recovery = SaveStore.RecoveryOf(canonical); RequireRemoval(recovery, request);
        foreach (var item in capture.Pawns)
        {
            var registry = item.Pawn.Registry;
            var row = await db.PawnCustody.AsNoTracking().SingleOrDefaultAsync(p => p.Id == registry.PawnCustodyId, ct);
            if (row is null || row.PlayerId != playerId || row.WorldId != request.Scope.WorldInstanceId
                || row.SaveGeneration != request.Scope.SaveGeneration || row.RestoreBoundary != request.Scope.RestoreBoundary
                || row.LocationKind != "Home" || !row.Tombstone || (row.AssignmentId ?? "") != registry.AssignmentId
                // Acceptance is outside its own XML frontier: the exact accepted removal link is also causal proof.
                || row.FrontierTransitionId != Guid.ParseExact(request.RemovalSnapshot.SnapshotId, "N").ToString("D")
                    && !recovery.Outposts.Metadata.Frontier.Any(f => f.TransitionId == row.FrontierTransitionId))
                throw Refuse(ErrorCode.RecoveryRequired, "ordinary-return-custody-conflict");
        }
    }

    internal static OutpostOrdinaryReturn CreateRecord(OrdinaryCancelRequest request, OrdinaryReturnPayload payload)
    {
        var capture = request.Capture;
        return new OutpostOrdinaryReturn { Id = request.ReturnAttemptId, WorldId = request.Scope.WorldInstanceId, PlayerId = request.Scope.OwnerPlayerId,
            SaveGeneration = request.Scope.SaveGeneration, SaveWriteEpoch = request.Scope.SaveWriteEpoch, RestoreBoundary = request.Scope.RestoreBoundary,
            RequestKind = (int)capture.RequestKind, RequestId = capture.ClientRequestId, LocalOperationId = capture.LocalOperationId,
            DerivedRequestKind = (int)(capture.DerivedRoute?.RequestKind ?? OrdinaryRequestKind.Unspecified),
            DerivedRequestId = capture.DerivedRoute?.ClientRequestId ?? "", Intent = request.ToByteArray(), Payload = payload.ToByteArray(),
            PayloadDigest = SHA256.HashData(payload.ToByteArray()) };
    }

    internal static async Task StageReturnCoreAsync(RimlinkDbContext db, OutpostOrdinaryReturn record, OrdinaryCancelRequest request, CancellationToken ct)
    {
        db.OutpostOrdinaryReturns.Add(record);
        foreach (var item in request.Capture.Pawns)
            await db.PawnCustody.Where(p => p.Id == item.Pawn.Registry.PawnCustodyId).ExecuteUpdateAsync(s =>
                s.SetProperty(p => p.LocationKind, "LocalReturnPending").SetProperty(p => p.LocationId, record.Id)
                 .SetProperty(p => p.FrontierTransitionId, record.Id).SetProperty(p => p.Revision, p => p.Revision + 1), ct);
    }

    internal static async Task RequireNoFenceAsync(RimlinkDbContext db, long actor, OrdinaryRequestKind kind, string requestId, CancellationToken ct)
    {
        if (await db.OutpostOrdinaryReturns.AsNoTracking().AnyAsync(r => r.PlayerId == actor
            && (r.RequestKind == (int)kind && r.RequestId == requestId || r.DerivedRequestKind == (int)kind && r.DerivedRequestId == requestId), ct))
            throw Refuse(ErrorCode.RecoveryRequired, "ordinary-operation-return-fenced");
    }

    internal static void RequireRemoval(SaveRecoveryData recovery, OrdinaryCancelRequest request)
    {
        if (!recovery.Outposts.Present || !recovery.Outposts.Metadata.Scope.Equals(request.Scope)
            || !recovery.Removed.Contains(request.Capture.LocalOperationId) || recovery.Returned.Contains(request.Capture.LocalOperationId)
            || !recovery.Outposts.Metadata.OrdinaryOutgoing.Any(c => c.Equals(request.Capture)))
            throw Refuse(ErrorCode.RecoveryRequired, "ordinary-return-removal-proof");
        foreach (var item in request.Capture.Pawns)
        {
            var original = item.Pawn.Registry;
            var recorded = recovery.Outposts.Metadata.Registry.SingleOrDefault(r => r.PawnCustodyId == original.PawnCustodyId && !r.NonAuthoritativeMirror);
            if (recorded is null || !recorded.Tombstone || recorded.AssignmentId != original.AssignmentId
                || recorded.PawnReferenceToken != original.PawnReferenceToken || !recorded.Scope.Equals(original.Scope))
                throw Refuse(ErrorCode.RecoveryRequired, "ordinary-return-registry-proof");
        }
    }

    internal static OrdinaryReturnPayload Payload(OrdinaryCancelRequest request)
    {
        try { return OrdinaryReturnCodec.Payload(request); }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { throw Refuse(ErrorCode.OutpostPayloadInvalid, "ordinary-return-invalid-payload"); }
    }

    private static IMessage Original(OrdinaryCancelRequest request) => OrdinaryReturnCodec.Original(request);

    private async Task<OrdinaryOperationReply> ReplyAsync(OutpostOrdinaryReturn row, OrdinaryCancelRequest request, CancellationToken ct)
    {
        var transition = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == row.Id, ct);
        return new() { Status = row.State == "Accepted" ? OrdinaryOperationStatus.ReturnAccepted : OrdinaryOperationStatus.ReturnStarted,
            ReturnProof = new() { Authorization = request.Clone(), TransitionId = row.Id, TransitionDigest = ByteString.CopyFrom(transition.Digest),
                PredecessorId = transition.PredecessorId ?? "", Payload = OrdinaryReturnPayload.Parser.ParseFrom(row.Payload), PayloadDigest = ByteString.CopyFrom(row.PayloadDigest) } };
    }
}
