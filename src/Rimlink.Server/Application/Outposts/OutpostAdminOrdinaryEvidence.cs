using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record OutpostAdminOrdinaryEffect(string ReturnAttemptId, int AuthorizationLength, byte[] AuthorizationSha256,
    int PayloadLength, byte[] PayloadSha256, OutpostAdminOrdinaryInputCommitment Input);

internal static partial class OutpostAdminResolveEvidence
{
    internal static async Task<OrdinaryOutgoingCapture> ReadOrdinaryTargetAsync(RimlinkDbContext db, SaveFiles files,
        OutpostAdminOrdinaryTarget target, CancellationToken ct)
    {
        if (!(target.ExpectedState is ("Unknown" or "Applied") && target.ExistingReturnAttemptId is null
                || target.ExpectedState is ("ReturnStarted" or "ReturnAccepted") && target.ExistingReturnAttemptId is not null) || target.Revision != 0
            || target.OperationId != target.Original.LocalOperationId || target.Original.Family != OutpostAdminOperationFamily.Ordinary) throw Invalid();
        var removed = await ReadOrdinarySnapshotAsync(db, files, target.RemovalSnapshot, ct);
        var canonical = await ReadOrdinarySnapshotAsync(db, files, target.CanonicalSnapshot, ct);
        var capture = removed.Outposts.Metadata.OrdinaryOutgoing.SingleOrDefault(c => c.LocalOperationId == target.OperationId);
        if (capture is null || !capture.Scope.Equals(target.Scope) || capture.RequestKind.ToString() != target.Original.Kind
            || capture.ClientRequestId != target.Original.RequestId || !capture.RequestIntentSha256.Span.SequenceEqual(target.Original.IntentDigest)
            || !(capture.DerivedRoute?.Equals(target.Original.DerivedRoute) ?? target.Original.DerivedRoute is null)
            || !canonical.Outposts.Metadata.OrdinaryOutgoing.Any(c => c.Equals(capture))) throw Invalid();
        return capture;
    }

    private static async Task<SaveRecoveryData> ReadOrdinarySnapshotAsync(RimlinkDbContext db, SaveFiles files, OutpostSnapshotProof proof, CancellationToken ct)
    {
        var s = proof.Scope;
        var link = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == s.WorldInstanceId
            && l.PlayerId == s.OwnerPlayerId && l.SaveGeneration == s.SaveGeneration && l.SaveWriteEpoch == s.SaveWriteEpoch
            && l.RestoreBoundary == s.RestoreBoundary && l.SnapshotId == proof.SnapshotId, ct);
        if (link is null || !link.RawSha256.AsSpan().SequenceEqual(proof.RawSha256.Span)) throw Invalid();
        var (version, archive) = await new OutpostLifecycleEvidence(db, files).ResolveSnapshotAsync(link, ct);
        var recovered = SaveRecoveryData.ReadXml(new MemoryStream(files.ReadEvidence(version, archive), false), out byte[] hash);
        if (!hash.AsSpan().SequenceEqual(proof.RawSha256.Span) || recovered.SnapshotId != proof.SnapshotId
            || !recovered.Outposts.Present || !recovered.Outposts.Metadata.Scope.Equals(s)
            || !recovered.Outposts.Metadata.ToByteArray().AsSpan().SequenceEqual(link.Metadata)) throw Invalid();
        return recovered;
    }

    internal static async Task<OrdinaryCancelRequest> ReadNewOrdinaryAsync(RimlinkDbContext db, SaveFiles files,
        OutpostAdminOrdinaryTarget selected, OriginalOrdinaryInput input, string transitionId, CancellationToken ct)
    {
        try
        {
            if (selected.ExpectedState != "Unknown" || selected.ExistingReturnAttemptId is not null) throw Invalid();
            var capture = await ReadOrdinaryTargetAsync(db, files, selected, ct);
            if (!input.Capture.Equals(capture) || !input.RemovalSnapshot.Equals(selected.RemovalSnapshot)
                || !input.CanonicalSnapshot.Equals(selected.CanonicalSnapshot)) throw Invalid();
            var request = OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(input, transitionId);
            if (selected.Input is not null && !SameCommitment(selected.Input, OutpostAdminIntentCodec.CommitOrdinaryInput(input, transitionId))) throw Invalid();
            await OrdinaryReturnService.RequireNoFenceAsync(db, selected.Scope.OwnerPlayerId, capture.RequestKind, capture.ClientRequestId, ct);
            if (capture.DerivedRoute is { } derived)
                await OrdinaryReturnService.RequireNoFenceAsync(db, selected.Scope.OwnerPlayerId, derived.RequestKind, derived.ClientRequestId, ct);
            if (await OrdinaryReturnService.SourceAsync(db, request, ct) is not null) throw Invalid();
            await OrdinaryReturnService.ValidateNegativeAsync(db, request, ct);
            return request;
        }
        catch (Exception error) when (error is RpcException or InvalidDataException or InvalidProtocolBufferException or ArgumentException) { throw Invalid(); }
    }

    internal static bool SameCommitment(OutpostAdminOrdinaryInputCommitment a, OutpostAdminOrdinaryInputCommitment b) =>
        a.Version == 1 && b.Version == 1 && a.InputLength == b.InputLength && a.OriginalLength == b.OriginalLength && a.AuthorizationLength == b.AuthorizationLength
        && a.InputDigest.AsSpan().SequenceEqual(b.InputDigest) && a.OriginalDigest.AsSpan().SequenceEqual(b.OriginalDigest)
        && a.AuthorizationDigest.AsSpan().SequenceEqual(b.AuthorizationDigest);

    internal static OutpostAdminEvidenceReference[] OrdinaryPrerequisites(OutpostTransition tip, OutpostAdminOrdinaryTarget selected, OrdinaryOutgoingCapture capture) =>
        new[] { new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Transition, tip.Id, (byte[])tip.Digest.Clone()),
            new(OutpostAdminEvidenceKind.OrdinarySource, selected.OperationId, SHA256.HashData(capture.ToByteArray())),
            new(OutpostAdminEvidenceKind.Snapshot, selected.RemovalSnapshot.SnapshotId, selected.RemovalSnapshot.RawSha256.ToByteArray()),
            new(OutpostAdminEvidenceKind.Snapshot, selected.CanonicalSnapshot.SnapshotId, selected.CanonicalSnapshot.RawSha256.ToByteArray()) }
            .DistinctBy(e => (e.Kind, e.Id)).OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();

    internal static async Task<OutpostOrdinaryReturn> StageOrdinaryHeldAsync(RimlinkDbContext db, SaveFiles files, OutpostLease lease,
        OutpostAuthority.OutpostDomainTransaction domain, OutpostAdminIntent intent, OriginalOrdinaryInput input, CancellationToken ct)
    {
        domain.RequireAdminStage(db, lease, intent.Allocation.TransitionId, intent.Allocation.At, "AdminResolved");
        var selected = intent.Recovery!.Ordinary!;
        if (!lease.CoversOwner(selected.Scope.OwnerPlayerId)) throw Invalid();
        var request = await ReadNewOrdinaryAsync(db, files, selected, input, intent.Allocation.TransitionId, ct);
        var row = OrdinaryReturnService.CreateRecord(request, OrdinaryReturnService.Payload(request));
        await OrdinaryReturnService.StageReturnCoreAsync(db, row, request, ct);
        await db.SaveChangesAsync(ct); // Flush only the caller-owned transaction, never commit it here.
        return row;
    }

    internal static byte[] OrdinaryBinding(OutpostAdminIntent intent, OutpostAdminCommandRecord receipt, OutpostOrdinaryReturn row)
    {
        var selected = intent.Recovery!.Ordinary!;
        var binding = new OutpostAdminResolvedBinding(4, "AdminResolved-v2", receipt.Id, (byte[])receipt.CommandDigest.Clone(),
            OutpostAdminBusinessSemantic.OrdinaryReturnStarted, selected.OperationId,
            new(selected.Original.Family, selected.Original.Kind, selected.Original.RequestId, selected.Original.LocalOperationId, (byte[])selected.Original.IntentDigest.Clone()),
            new(selected.Scope.WorldInstanceId, selected.Scope.OwnerPlayerId, selected.Scope.SaveGeneration, selected.Scope.SaveWriteEpoch, selected.Scope.RestoreBoundary),
            selected.ExpectedState, selected.Revision, new(OutpostAdminIntentTable.OrdinaryReturn, row.Id, "Intent", row.Intent.Length, SHA256.HashData(row.Intent)),
            intent.Evidence, null, SHA256.HashData(receipt.Result), Ordinary: new(row.Id, row.Intent.Length, SHA256.HashData(row.Intent), row.Payload.Length,
                (byte[])row.PayloadDigest.Clone(), selected.Input!));
        return OutpostRestoreEvidence.Encode(binding);
    }

    private static async Task<OutpostAdminSemanticView> ReadOrdinarySemanticAsync(RimlinkDbContext db, SaveFiles files, OutpostTransition raw,
        OutpostAdminResolvedBinding binding, OutpostAdminIntent intent, OutpostAdminCommandRecord receipt,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        var selected = intent.Recovery?.Ordinary;
        if (intent.Version != 2 || selected?.Input is null || binding.Semantic != OutpostAdminBusinessSemantic.OrdinaryReturnStarted) throw Invalid();
        var row = await db.OutpostOrdinaryReturns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == raw.Id
            && r.Intent.Length >= 1 && r.Intent.Length <= Limits.MaxMessageBytes && r.Payload.Length <= Limits.EconomyMaxParcelBytes, ct);
        if (row is null) throw Invalid();
        OrdinaryCancelRequest candidate;
        try
        {
            using var parser = CodedInputStream.CreateWithLimits(new MemoryStream(row.Intent, false), Limits.MaxMessageBytes, 32);
            candidate = OrdinaryCancelRequest.Parser.ParseFrom(parser);
            var input = new OriginalOrdinaryInput(1, candidate.OriginalRequest.ToByteArray(), candidate.Capture, candidate.RemovalSnapshot, candidate.CanonicalSnapshot);
            if (candidate.ReturnAttemptId != raw.Id || !OutpostAdminIntentCodec.ComposeOrdinaryAuthorization(input, raw.Id).Equals(candidate)
                || !SameCommitment(selected.Input, OutpostAdminIntentCodec.CommitOrdinaryInput(input, raw.Id))) throw Invalid();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException) { throw Invalid(); }
        var capture = await ReadOrdinaryTargetAsync(db, files, selected, ct);
        if (!candidate.Capture.Equals(capture) || !candidate.RemovalSnapshot.Equals(selected.RemovalSnapshot)
            || !candidate.CanonicalSnapshot.Equals(selected.CanonicalSnapshot) || !candidate.Scope.Equals(selected.Scope)
            || row.WorldId != raw.WorldId || row.PlayerId != raw.PlayerId || row.SaveGeneration != raw.SaveGeneration
            || row.SaveWriteEpoch != selected.Scope.SaveWriteEpoch || row.RestoreBoundary != raw.RestoreBoundary
            || !OrdinaryBinding(intent, receipt, row).AsSpan().SequenceEqual(raw.Intent)) throw Invalid();
        if (!transitions.TryGetValue(raw.PredecessorId ?? "", out var prior)) throw Invalid();
        var refs = OrdinaryPrerequisites(prior, selected, capture);
        if (refs.Length != intent.Evidence.Length || !refs.Zip(intent.Evidence).All(pair => pair.First.Kind == pair.Second.Kind
            && pair.First.Id == pair.Second.Id && (pair.First.Digest ?? []).AsSpan().SequenceEqual(pair.Second.Digest))) throw Invalid();
        // Both accepted snapshot transitions must precede the raw administrative effect, never be future/self evidence.
        var required = new HashSet<string>(new[] { selected.RemovalSnapshot, selected.CanonicalSnapshot }
            .Select(p => Guid.ParseExact(p.SnapshotId, "N").ToString("D")), StringComparer.Ordinal);
        string? cursor = raw.PredecessorId; int remaining = transitions.Count;
        while (required.Count > 0 && cursor is not null && remaining-- > 0 && transitions.TryGetValue(cursor, out var ancestor))
        { required.Remove(cursor); cursor = ancestor.PredecessorId; }
        if (required.Count != 0) throw Invalid();
        return new(raw, binding.Semantic, "OrdinaryReturnStarted", (byte[])row.Intent.Clone());
    }
}
