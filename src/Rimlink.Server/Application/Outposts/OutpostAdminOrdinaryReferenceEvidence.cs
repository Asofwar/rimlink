using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record OutpostAdminOrdinaryReference(string SourceTransitionId, byte[] SourceTransitionDigest,
    string ReturnAttemptId, int AuthorizationLength, byte[] AuthorizationSha256, int PayloadLength, byte[] PayloadSha256,
    OrdinaryOperationStatus Status, string? AcceptedSnapshotId);
internal sealed record OutpostAdminOrdinaryReferenceEvidence(OutpostOrdinaryReturn Row, OrdinaryCancelRequest Authorization,
    OrdinaryOutgoingCapture Capture, OutpostTransition Source, OutpostSnapshotLink? Accepted);

internal static partial class OutpostAdminResolveEvidence
{
    internal static async Task<OutpostAdminOrdinaryReferenceEvidence> ReadOrdinaryReferenceAsync(RimlinkDbContext db, SaveFiles files,
        OutpostAdminOrdinaryTarget selected, bool historical, CancellationToken ct)
    {
        if (selected.Input is not null || selected.ExpectedState is not ("ReturnStarted" or "ReturnAccepted")
            || selected.ExistingReturnAttemptId is null) throw Invalid();
        var capture = await ReadOrdinaryTargetAsync(db, files, selected, ct);
        var row = await db.OutpostOrdinaryReturns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == selected.ExistingReturnAttemptId
            && r.Intent.Length >= 1 && r.Intent.Length <= Limits.MaxMessageBytes && r.Payload.Length >= 1
            && r.Payload.Length <= Limits.EconomyMaxParcelBytes, ct);
        if (row is null || row.WorldId != selected.Scope.WorldInstanceId || row.PlayerId != selected.Scope.OwnerPlayerId
            || row.SaveGeneration != selected.Scope.SaveGeneration || row.SaveWriteEpoch != selected.Scope.SaveWriteEpoch
            || row.RestoreBoundary != selected.Scope.RestoreBoundary || row.RequestKind != (int)capture.RequestKind
            || row.RequestId != capture.ClientRequestId || row.LocalOperationId != capture.LocalOperationId
            || row.DerivedRequestKind != (int)(capture.DerivedRoute?.RequestKind ?? OrdinaryRequestKind.Unspecified)
            || row.DerivedRequestId != (capture.DerivedRoute?.ClientRequestId ?? "") || row.State is not ("Started" or "Accepted")
            || selected.ExpectedState == "ReturnAccepted" && row.State != "Accepted"
            || !historical && selected.ExpectedState == "ReturnStarted" && row.State != "Started") throw Invalid();
        OrdinaryCancelRequest authorization;
        try
        {
            using var parser = CodedInputStream.CreateWithLimits(new MemoryStream(row.Intent, false), Limits.MaxMessageBytes, 32);
            authorization = OrdinaryCancelRequest.Parser.ParseFrom(parser);
            var payload = OrdinaryReturnCodec.Payload(authorization).ToByteArray();
            if (!payload.AsSpan().SequenceEqual(row.Payload) || !SHA256.HashData(payload).AsSpan().SequenceEqual(row.PayloadDigest)) throw Invalid();
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidDataException or ArgumentException) { throw Invalid(); }
        if (authorization.ReturnAttemptId != row.Id || !authorization.Scope.Equals(selected.Scope)
            || !authorization.Capture.Equals(capture) || !authorization.RemovalSnapshot.Equals(selected.RemovalSnapshot)
            || !authorization.CanonicalSnapshot.Equals(selected.CanonicalSnapshot)) throw Invalid();
        var source = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == row.Id, ct);
        if (source is null || source.WorldId != row.WorldId || source.PlayerId != row.PlayerId || source.SaveGeneration != row.SaveGeneration
            || source.RestoreBoundary != row.RestoreBoundary || source.Digest.Length != 32
            || !(source.Kind == "OrdinaryReturnStarted" && source.Intent.AsSpan().SequenceEqual(row.Intent)
                || source.Kind == "AdminResolved" && Read(source).Semantic == OutpostAdminBusinessSemantic.OrdinaryReturnStarted)) throw Invalid();
        OutpostSnapshotLink? accepted = null;
        if (selected.ExpectedState == "ReturnAccepted")
        {
            accepted = await db.OutpostSnapshotLinks.AsNoTracking().SingleOrDefaultAsync(l => l.WorldId == row.WorldId && l.PlayerId == row.PlayerId
                && l.SaveGeneration == row.SaveGeneration && l.SaveWriteEpoch == row.SaveWriteEpoch && l.RestoreBoundary == row.RestoreBoundary
                && l.SnapshotId == row.AcceptedSnapshotId, ct);
            if (accepted is null || accepted.RawSha256.Length != 32) throw Invalid();
            var proof = new OutpostSnapshotProof { Scope = selected.Scope.Clone(), SnapshotId = accepted.SnapshotId,
                RawSha256 = ByteString.CopyFrom(accepted.RawSha256) };
            var actual = await ReadOrdinarySnapshotAsync(db, files, proof, ct);
            if (!actual.Returned.Contains(capture.LocalOperationId) || !actual.Outposts.Metadata.Receipts.Any(r => r.OrdinaryReturnAttemptId == row.Id
                    && r.OrdinaryLocalOperationId == capture.LocalOperationId && r.TransitionId == source.Id
                    && r.TransitionDigest.Span.SequenceEqual(source.Digest))) throw Invalid();
        }
        return new(row, authorization, capture, source, accepted);
    }

    internal static void ValidateReferenceInput(OutpostAdminOrdinaryReferenceEvidence source, OriginalOrdinaryInput? supplied)
    {
        if (supplied is null) return;
        var actual = new OriginalOrdinaryInput(1, source.Authorization.OriginalRequest.ToByteArray(), source.Authorization.Capture,
            source.Authorization.RemovalSnapshot, source.Authorization.CanonicalSnapshot);
        if (!OutpostAdminIntentCodec.EncodeOrdinaryInput(actual).AsSpan().SequenceEqual(OutpostAdminIntentCodec.EncodeOrdinaryInput(supplied))) throw Invalid();
    }

    internal static OutpostAdminEvidenceReference[] OrdinaryReferencePrerequisites(OutpostTransition prior,
        OutpostAdminOrdinaryTarget selected, OutpostAdminOrdinaryReferenceEvidence source) =>
        OrdinaryPrerequisites(prior, selected, source.Capture)
            .Append(new(OutpostAdminEvidenceKind.Transition, source.Source.Id, (byte[])source.Source.Digest.Clone()))
            .Append(new(OutpostAdminEvidenceKind.Operation, source.Row.Id, SHA256.HashData(source.Row.Intent)))
            .Concat(source.Accepted is null ? [] : new[] { new OutpostAdminEvidenceReference(OutpostAdminEvidenceKind.Snapshot,
                source.Accepted.SnapshotId, (byte[])source.Accepted.RawSha256.Clone()) })
            .DistinctBy(e => (e.Kind, e.Id)).OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();

    internal static byte[] OrdinaryReferenceBinding(OutpostAdminIntent intent, OutpostAdminCommandRecord receipt,
        OutpostAdminOrdinaryReferenceEvidence source)
    {
        var selected = intent.Recovery!.Ordinary!;
        var binding = new OutpostAdminResolvedBinding(5, "AdminResolved-v3", receipt.Id, (byte[])receipt.CommandDigest.Clone(),
            OutpostAdminBusinessSemantic.NoNewBusinessEffect, selected.OperationId,
            new(selected.Original.Family, selected.Original.Kind, selected.Original.RequestId, selected.Original.LocalOperationId, (byte[])selected.Original.IntentDigest.Clone()),
            new(selected.Scope.WorldInstanceId, selected.Scope.OwnerPlayerId, selected.Scope.SaveGeneration, selected.Scope.SaveWriteEpoch, selected.Scope.RestoreBoundary),
            selected.ExpectedState, selected.Revision, new(OutpostAdminIntentTable.OrdinaryReturn, source.Row.Id, "Intent", source.Row.Intent.Length, SHA256.HashData(source.Row.Intent)),
            intent.Evidence, null, SHA256.HashData(receipt.Result), Reference: new(source.Source.Id, (byte[])source.Source.Digest.Clone(), source.Row.Id,
                source.Row.Intent.Length, SHA256.HashData(source.Row.Intent), source.Row.Payload.Length, (byte[])source.Row.PayloadDigest.Clone(),
                selected.ExpectedState == "ReturnAccepted" ? OrdinaryOperationStatus.ReturnAccepted : OrdinaryOperationStatus.ReturnStarted,
                source.Accepted?.SnapshotId));
        return OutpostRestoreEvidence.Encode(binding);
    }

    private static async Task<OutpostAdminSemanticView> ReadOrdinaryReferenceSemanticAsync(RimlinkDbContext db, SaveFiles files,
        OutpostTransition raw, OutpostAdminIntent intent, OutpostAdminCommandRecord receipt,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        if (intent.Version != 2 || intent.Recovery?.Ordinary is not { Input: null } selected) throw Invalid();
        var source = await ReadOrdinaryReferenceAsync(db, files, selected, historical: true, ct);
        if (source.Source.Id == raw.Id || !transitions.TryGetValue(source.Source.Id, out var original)
            || !original.Digest.AsSpan().SequenceEqual(source.Source.Digest)) throw Invalid();
        if (original.Kind == "AdminResolved")
        {
            var originalView = await ReadValidatedAsync(db, files, original, transitions, ct);
            if (originalView.Semantic != OutpostAdminBusinessSemantic.OrdinaryReturnStarted
                || originalView.BusinessKind != "OrdinaryReturnStarted" || originalView.BusinessIntent is null
                || !originalView.BusinessIntent.AsSpan().SequenceEqual(source.Row.Intent)) throw Invalid();
        }
        if (!transitions.TryGetValue(raw.PredecessorId ?? "", out var prior)) throw Invalid();
        var refs = OrdinaryReferencePrerequisites(prior, selected, source);
        if (refs.Length != intent.Evidence.Length || !refs.Zip(intent.Evidence).All(pair => pair.First.Kind == pair.Second.Kind
            && pair.First.Id == pair.Second.Id && (pair.First.Digest ?? []).AsSpan().SequenceEqual(pair.Second.Digest))
            || !OrdinaryReferenceBinding(intent, receipt, source).AsSpan().SequenceEqual(raw.Intent)) throw Invalid();
        var required = new HashSet<string>(new[] { source.Source.Id,
            Guid.ParseExact(selected.RemovalSnapshot.SnapshotId, "N").ToString("D"),
            Guid.ParseExact(selected.CanonicalSnapshot.SnapshotId, "N").ToString("D") }, StringComparer.Ordinal);
        if (source.Accepted is not null) required.Add(Guid.ParseExact(source.Accepted.SnapshotId, "N").ToString("D"));
        string? cursor = raw.PredecessorId; int remaining = transitions.Count;
        while (required.Count > 0 && cursor is not null && remaining-- > 0 && transitions.TryGetValue(cursor, out var ancestor))
        { required.Remove(cursor); cursor = ancestor.PredecessorId; }
        if (required.Count != 0) throw Invalid();
        return new(raw, OutpostAdminBusinessSemantic.NoNewBusinessEffect); // Deliberately no business kind, bytes or grant.
    }
}
