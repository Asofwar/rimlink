using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record OutpostAdminCancellationEvidence(OutpostOperation Operation, OutpostPrepareRequest Intent,
    bool NoRemoval, OutpostTransition Prerequisite, OutpostSnapshotProof Proof);
internal sealed record OutpostAdminCancellationProof(string SnapshotId, byte[] RawSha256, OutpostAdminBusinessScope Scope);
internal sealed record OutpostAdminCancellationEffect(string EffectTransitionId, OutpostAdminCancellationProof Proof,
    string PrerequisiteId, byte[] PrerequisiteDigest, int ResultLength, byte[] ResultSha256,
    long Revision, OutpostAdminResolvedParcel[] Parcels);

internal static partial class OutpostAdminResolveEvidence
{
    internal static async Task<OutpostOperation> ReadSelectionAsync(RimlinkDbContext db, SaveFiles files,
        OutpostAdminResolutionSelection selected, CancellationToken ct) => selected.Kind == OutpostAdminResolutionKind.ExistingTerminal
        ? (await ReadTerminalAsync(db, files, selected, ct)).Operation
        : (await ReadCancellationAsync(db, files, selected, current: true, ct)).Operation;

    internal static async Task<OutpostAdminCancellationEvidence> ReadCancellationAsync(RimlinkDbContext db, SaveFiles files,
        OutpostAdminResolutionSelection selected, bool current, CancellationToken ct)
    {
        bool noRemoval = selected.Kind == OutpostAdminResolutionKind.AbortNoRemoval;
        if ((!noRemoval && selected.Kind != OutpostAdminResolutionKind.AbortWithReturn)
            || selected.Original.Family != OutpostAdminOperationFamily.Outpost || selected.Original.LocalOperationId is not null
            || selected.Original.DerivedRoute is not null || selected.CanonicalSnapshot is not { } proof || !proof.Scope.Equals(selected.Scope)) throw Invalid();
        var operation = await db.OutpostOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == selected.OperationId
            && o.Intent.Length >= 1 && o.Intent.Length <= Limits.EconomyMaxParcelBytes && o.Result.Length <= Limits.MaxMessageBytes, ct);
        if (operation is null || operation.Kind is not ("Found" or "Assign") || operation.Kind != selected.Original.Kind
            || operation.RequestId != selected.Original.RequestId || !operation.IntentDigest.AsSpan().SequenceEqual(selected.Original.IntentDigest)
            || operation.WorldId != selected.Scope.WorldInstanceId || operation.PlayerId != selected.Scope.OwnerPlayerId
            || operation.SaveGeneration != selected.Scope.SaveGeneration || operation.SaveWriteEpoch != selected.Scope.SaveWriteEpoch
            || operation.RestoreBoundary != selected.Scope.RestoreBoundary
            || (noRemoval ? selected.ExpectedState != "CancelFenced" : selected.ExpectedState != "Prepared")
            || current && operation.State != selected.ExpectedState) throw Invalid();
        OutpostPrepareRequest intent;
        try { intent = OutpostAssignmentService.ParseIntent(operation); }
        catch (InvalidProtocolBufferException) { throw Invalid(); }
        if (!intent.Scope.Equals(selected.Scope) || intent.Kind.ToString() != operation.Kind || intent.ClientRequestId != operation.RequestId
            || intent.OutpostId != operation.OutpostId || !intent.IntentDigest.Span.SequenceEqual(operation.IntentDigest)
            || !OutpostRequestIdentity.Digest(intent).Equals(intent.IntentDigest)) throw Invalid();
        var prepared = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == operation.Id, ct);
        if (prepared is null || prepared.Kind != "Prepare" || !SameOperation(prepared, operation)) throw Invalid();
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == operation.WorldId
            && l.PlayerId == operation.PlayerId && l.SaveGeneration == operation.SaveGeneration && l.SaveWriteEpoch == operation.SaveWriteEpoch
            && l.RestoreBoundary == operation.RestoreBoundary && l.SnapshotId == proof.SnapshotId, ct);
        if (link is null || proof.RawSha256.Length != 32 || !link.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span)) throw Invalid();
        if (current && !await db.SaveVersions.AsNoTracking().AnyAsync(v => v.PlayerId == operation.PlayerId
            && v.SaveGeneration == operation.SaveGeneration && v.IsCurrent && v.SnapshotId == proof.SnapshotId, ct)) throw Invalid();
        var (version, archive) = await new OutpostLifecycleEvidence(db, files).ResolveSnapshotAsync(link, ct);
        var recovered = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(version, archive), writable: false), out byte[] rawSha);
        var metadata = recovered.Outposts.Metadata;
        if (!rawSha.AsSpan().SequenceEqual(link.RawSha256) || version.RawSha256?.Length != 32 || !rawSha.AsSpan().SequenceEqual(version.RawSha256)
            || !recovered.Outposts.Present || recovered.SnapshotId != proof.SnapshotId || recovered.Generation != operation.SaveGeneration
            || recovered.WriteEpoch != operation.SaveWriteEpoch || !metadata.Scope.Equals(selected.Scope)
            || !metadata.ToByteArray().AsSpan().SequenceEqual(link.Metadata)) throw Invalid();
        var receipt = metadata.Receipts.SingleOrDefault(r => r.ClientRequestId == operation.RequestId
            && r.IntentDigest.Span.SequenceEqual(operation.IntentDigest) && r.CancellationKind == (noRemoval ? OutpostCancelKind.AbortNoRemoval : OutpostCancelKind.Unspecified));
        if (receipt is null || !receipt.Snapshot.Equals(intent.PreRemoval) || receipt.DeliveryAttemptId.Length != 0 || receipt.ParcelIds.Count != 0
            || !receipt.PawnCustodyIds.Order().SequenceEqual(intent.Workers.Select(w => w.PawnCustodyId).Order())
            || !receipt.AssignmentIds.Order().SequenceEqual(intent.Workers.Select(w => w.AssignmentId).Order())
            || (noRemoval ? receipt.NoRemovalBranchId != intent.Caravan.GameBranchId : receipt.NoRemovalBranchId.Length != 0 || receipt.TransitionId != operation.Id)) throw Invalid();
        var prerequisite = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == receipt.TransitionId, ct);
        if (prerequisite is null || prerequisite.Kind != (noRemoval ? "CancelFence" : "Prepare") || !SameOperation(prerequisite, operation)
            || current && noRemoval && operation.TransitionId != prerequisite.Id
            || !prerequisite.Digest.AsSpan().SequenceEqual(receipt.TransitionDigest.Span) || (prerequisite.PredecessorId ?? "") != receipt.PredecessorId
            || !metadata.Frontier.Any(f => f.TransitionId == prerequisite.Id && f.TransitionDigest.Equals(receipt.TransitionDigest)
                && f.PredecessorId == receipt.PredecessorId)) throw Invalid();
        foreach (var worker in intent.Workers)
        {
            var registry = metadata.Registry.SingleOrDefault(e => !e.NonAuthoritativeMirror && e.PawnCustodyId == worker.PawnCustodyId);
            if (registry is null || !registry.Scope.Equals(worker.Pawn.Registry.Scope)
                || (noRemoval ? !registry.Equals(worker.Pawn.Registry)
                    : !registry.Tombstone || registry.AssignmentId != worker.AssignmentId || registry.PawnReferenceToken.Length != 0)) throw Invalid();
        }
        if (current)
        {
            var outpost = await db.Outposts.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operation.OutpostId, ct);
            if (outpost is null || outpost.Revision != selected.Revision || outpost.Revision == long.MaxValue
                || operation.ReservedSilver != (operation.Kind == "Found" ? 500 : 0)) throw Invalid();
        }
        return new(operation, intent, noRemoval, prerequisite, proof.Clone());
    }

    private static bool SameOperation(OutpostTransition transition, OutpostOperation operation) => transition.WorldId == operation.WorldId
        && transition.PlayerId == operation.PlayerId && transition.SaveGeneration == operation.SaveGeneration
        && transition.RestoreBoundary == operation.RestoreBoundary && transition.Intent.AsSpan().SequenceEqual(operation.Intent);

    internal static async Task<byte[]> CancellationBindingAsync(RimlinkDbContext db, OutpostAdminIntent intent,
        OutpostAdminCommandRecord receipt, OutpostAdminCancellationEvidence source, CancellationToken ct)
    {
        var operation = await db.OutpostOperations.AsNoTracking().SingleAsync(o => o.Id == source.Operation.Id, ct);
        OutpostOperationReply reply;
        try { reply = OutpostOperationReply.Parser.ParseFrom(operation.Result); }
        catch (InvalidProtocolBufferException) { throw Invalid(); }
        var parcels = await db.OutpostParcelLinks.AsNoTracking().Where(p => p.BatchId == intent.Allocation.TransitionId)
            .OrderBy(p => p.ParcelId).Take(OutpostAdminIntentCodec.MaximumReferences + 1).ToArrayAsync(ct);
        if (operation.State != "Cancelled" || operation.ReservedSilver != 0 || operation.TransitionId != intent.Allocation.TransitionId
            || !operation.Intent.AsSpan().SequenceEqual(source.Operation.Intent) || !operation.IntentDigest.AsSpan().SequenceEqual(source.Operation.IntentDigest)
            || !reply.Scope.Equals(source.Intent.Scope) || reply.ClientRequestId != operation.RequestId || reply.Status != OutpostOperationStatus.Cancelled
            || reply.TransitionId != operation.TransitionId || reply.TransitionDigest.Length != 0 || !reply.PreparedIntent.Equals(source.Intent)
            || !reply.IntentDigest.Equals(source.Intent.IntentDigest) || reply.AuthoritativeRevision != checked(intent.Resolution!.Revision + 1)
            || parcels.Length != (source.NoRemoval ? 0 : 1) || !reply.ParcelIds.SequenceEqual(parcels.Select(p => p.ParcelId))) throw Invalid();
        foreach (var parcel in parcels)
        {
            var payload = OutpostParcelPayload.Parser.ParseFrom(parcel.Payload);
            if (parcel.TransitionId != operation.TransitionId || parcel.WorldId != operation.WorldId || parcel.PlayerId != operation.PlayerId
                || parcel.SaveGeneration != operation.SaveGeneration || parcel.RestoreBoundary != operation.RestoreBoundary
                || !SHA256.HashData(parcel.Payload).AsSpan().SequenceEqual(parcel.PayloadDigest) || payload.BatchOperationId != operation.TransitionId
                || payload.Kind != OutpostPayloadKind.Workers || !payload.Scope.Equals(source.Intent.Scope) || payload.OutpostId != operation.OutpostId
                || payload.ManifestId != source.Intent.ManifestId || !payload.ManifestDigest.Equals(source.Intent.ManifestDigest)
                || payload.ChildCount != 1 || !payload.PawnCustodyIds.SequenceEqual(source.Intent.Workers.Select(w => w.PawnCustodyId))
                || !payload.AssignmentIds.SequenceEqual(source.Intent.Workers.Select(w => w.AssignmentId))) throw Invalid();
        }
        var selected = intent.Resolution!;
        return OutpostRestoreEvidence.Encode(new OutpostAdminResolvedBinding(3, "AdminResolved-v1", receipt.Id, receipt.CommandDigest,
            source.NoRemoval ? OutpostAdminBusinessSemantic.AbortNoRemoval : OutpostAdminBusinessSemantic.AbortWithReturn,
            operation.Id, new(selected.Original.Family, selected.Original.Kind, selected.Original.RequestId, selected.Original.LocalOperationId, selected.Original.IntentDigest),
            new(selected.Scope.WorldInstanceId, selected.Scope.OwnerPlayerId, selected.Scope.SaveGeneration, selected.Scope.SaveWriteEpoch, selected.Scope.RestoreBoundary),
            selected.ExpectedState, selected.Revision, new(OutpostAdminIntentTable.OutpostOperation, operation.Id, "Intent", operation.Intent.Length, SHA256.HashData(operation.Intent)),
            intent.Evidence, null, SHA256.HashData(receipt.Result), new(operation.TransitionId!,
                new(source.Proof.SnapshotId, source.Proof.RawSha256.ToByteArray(), new(source.Proof.Scope.WorldInstanceId, source.Proof.Scope.OwnerPlayerId,
                    source.Proof.Scope.SaveGeneration, source.Proof.Scope.SaveWriteEpoch, source.Proof.Scope.RestoreBoundary)), source.Prerequisite.Id,
                source.Prerequisite.Digest, operation.Result.Length, SHA256.HashData(operation.Result), reply.AuthoritativeRevision,
                parcels.Select(p => new OutpostAdminResolvedParcel(p.ParcelId, p.Payload.Length, p.PayloadDigest)).ToArray())));
    }
}
