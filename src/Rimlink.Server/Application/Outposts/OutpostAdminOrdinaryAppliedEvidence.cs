using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal sealed record OutpostAdminOrdinaryApplied(string SourceTransitionId, byte[] SourceTransitionDigest,
    long SourceId, OrdinaryRequestKind Route);
internal sealed record OutpostAdminOrdinaryAppliedEvidence(OrdinaryOutgoingCapture Capture, OutpostTransition Source,
    CustodyTransferEvidence Part);

public sealed partial class PawnCustodyBridge
{
    internal static async Task<OutpostAdminOrdinaryAppliedEvidence> ReadOrdinaryAppliedAsync(RimlinkDbContext db,
        OrdinaryOutgoingCapture capture, OutpostSnapshotProof removal, CancellationToken ct)
    {
        var candidates = db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "CustodyTransfer"
            && t.WorldId == capture.Scope.WorldInstanceId && t.RestoreBoundary == capture.Scope.RestoreBoundary);
        if (await candidates.CountAsync(ct) > OutpostSnapshotGuard.MaxSnapshots
            || await candidates.AnyAsync(t => t.Intent.Length < 1 || t.Intent.Length > Limits.MaxMessageBytes, ct)
            || await candidates.SumAsync(t => (long)t.Intent.Length, ct) > OutpostSnapshotGuard.MaxRetainedMetadataBytes)
            throw new OutpostEvidenceUnavailableException("ordinary-source-history-unavailable");
        OutpostAdminOrdinaryAppliedEvidence? found = null;
        await foreach (var raw in candidates.AsAsyncEnumerable().WithCancellation(ct))
        {
            using var input = CodedInputStream.CreateWithLimits(new MemoryStream(raw.Intent, false), Limits.MaxMessageBytes, 32);
            var root = CustodyTransferEvidence.Parser.ParseFrom(input);
            foreach (var part in Parts(root).Where(p => p.OrdinaryCapture?.Equals(capture) == true))
            {
                if (part.NonAuthoritativeMirror || part.ActorPlayerId != capture.Scope.OwnerPlayerId || part.SourceId <= 0
                    || !part.RemovalSnapshot.Equals(removal) || !OrdinaryRouteMatches(part.Kind, part.OrdinaryRequestKind)
                    || !OrdinaryCapture.Matches(capture, part.OrdinaryRequestKind, part.ClientRequestId, part.SourceRequestIntent.Span)
                    || !part.Pawns.Pawns.Where(p => p.Registry is not null).SequenceEqual(capture.Pawns.Select(p => p.Pawn))
                    || !await SourceMatchesAsync(db, part, ct) || found is not null)
                    throw new OutpostEvidenceUnavailableException("ordinary-source-history-unavailable");
                await ValidateStoredEvidenceAsync(db, [raw], ct);
                found = new(capture, raw, part);
            }
        }
        return found ?? throw new OutpostEvidenceUnavailableException("ordinary-source-history-unavailable");
    }
}

internal static partial class OutpostAdminResolveEvidence
{
    internal static async Task<OutpostAdminOrdinaryAppliedEvidence> ReadAppliedAsync(RimlinkDbContext db, SaveFiles files,
        OutpostAdminOrdinaryTarget selected, CancellationToken ct)
    {
        if (selected is not { ExpectedState: "Applied", Input: null, ExistingReturnAttemptId: null }) throw Invalid();
        var capture = await ReadOrdinaryTargetAsync(db, files, selected, ct);
        try
        {
            await OrdinaryReturnService.RequireNoFenceAsync(db, selected.Scope.OwnerPlayerId, capture.RequestKind, capture.ClientRequestId, ct);
            if (capture.DerivedRoute is { } derived)
                await OrdinaryReturnService.RequireNoFenceAsync(db, selected.Scope.OwnerPlayerId, derived.RequestKind, derived.ClientRequestId, ct);
            return await PawnCustodyBridge.ReadOrdinaryAppliedAsync(db, capture, selected.RemovalSnapshot, ct);
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidDataException or global::Grpc.Core.RpcException) { throw Invalid(); }
    }

    internal static void ValidateAppliedInput(OutpostAdminOrdinaryTarget selected, OrdinaryOutgoingCapture capture, OriginalOrdinaryInput? input)
    {
        if (input is not null && (!input.Capture.Equals(capture) || !input.RemovalSnapshot.Equals(selected.RemovalSnapshot)
            || !input.CanonicalSnapshot.Equals(selected.CanonicalSnapshot))) throw Invalid();
    }

    internal static OutpostAdminEvidenceReference[] AppliedPrerequisites(OutpostTransition prior,
        OutpostAdminOrdinaryTarget selected, OutpostAdminOrdinaryAppliedEvidence source) =>
        OrdinaryPrerequisites(prior, selected, source.Capture)
            .Append(new(OutpostAdminEvidenceKind.Transition, source.Source.Id, (byte[])source.Source.Digest.Clone()))
            .DistinctBy(e => (e.Kind, e.Id)).OrderBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();

    internal static byte[] AppliedBinding(OutpostAdminIntent intent, OutpostAdminCommandRecord receipt,
        OutpostAdminOrdinaryAppliedEvidence source)
    {
        var s = intent.Recovery!.Ordinary!;
        return OutpostRestoreEvidence.Encode(new OutpostAdminResolvedBinding(6, "AdminResolved-v4", receipt.Id, (byte[])receipt.CommandDigest.Clone(),
            OutpostAdminBusinessSemantic.NoNewBusinessEffect, s.OperationId,
            new(s.Original.Family, s.Original.Kind, s.Original.RequestId, s.Original.LocalOperationId, (byte[])s.Original.IntentDigest.Clone()),
            new(s.Scope.WorldInstanceId, s.Scope.OwnerPlayerId, s.Scope.SaveGeneration, s.Scope.SaveWriteEpoch, s.Scope.RestoreBoundary),
            s.ExpectedState, s.Revision, new(OutpostAdminIntentTable.CustodyTransition, source.Source.Id, "Intent", source.Source.Intent.Length, SHA256.HashData(source.Source.Intent)),
            intent.Evidence, null, SHA256.HashData(receipt.Result), Applied: new(source.Source.Id, (byte[])source.Source.Digest.Clone(), source.Part.SourceId, source.Part.OrdinaryRequestKind)));
    }

    private static async Task<OutpostAdminSemanticView> ReadAppliedSemanticAsync(RimlinkDbContext db, SaveFiles files,
        OutpostTransition raw, OutpostAdminIntent intent, OutpostAdminCommandRecord receipt,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        if (intent.Version != 2 || intent.Recovery?.Ordinary is not { ExpectedState: "Applied", Input: null } selected) throw Invalid();
        var source = await ReadAppliedAsync(db, files, selected, ct);
        var result = OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(receipt.Result);
        if (result.OrdinaryStatus != OrdinaryOperationStatus.Applied || !result.NoReplayAuthorization
            || result.References is not [{ Kind: OutpostAdminEvidenceKind.Transition } reference]
            || reference.Id != source.Source.Id || reference.Digest is null
            || !reference.Digest.AsSpan().SequenceEqual(source.Source.Digest)) throw Invalid();
        if (!transitions.TryGetValue(raw.PredecessorId ?? "", out var prior)
            || !transitions.TryGetValue(source.Source.Id, out var original) || original.Kind != "CustodyTransfer"
            || !original.Digest.AsSpan().SequenceEqual(source.Source.Digest)) throw Invalid();
        var refs = AppliedPrerequisites(prior, selected, source);
        if (refs.Length != intent.Evidence.Length || !refs.Zip(intent.Evidence).All(p => p.First.Kind == p.Second.Kind
            && p.First.Id == p.Second.Id && (p.First.Digest ?? []).AsSpan().SequenceEqual(p.Second.Digest))
            || !AppliedBinding(intent, receipt, source).AsSpan().SequenceEqual(raw.Intent)) throw Invalid();
        var required = new HashSet<string>(new[] { source.Source.Id, Guid.ParseExact(selected.RemovalSnapshot.SnapshotId, "N").ToString("D"),
            Guid.ParseExact(selected.CanonicalSnapshot.SnapshotId, "N").ToString("D") }, StringComparer.Ordinal);
        string? cursor = raw.PredecessorId; int remaining = transitions.Count;
        while (required.Count > 0 && cursor is not null && remaining-- > 0 && transitions.TryGetValue(cursor, out var ancestor))
        { required.Remove(cursor); cursor = ancestor.PredecessorId; }
        if (required.Count != 0) throw Invalid();
        return new(raw, OutpostAdminBusinessSemantic.NoNewBusinessEffect);
    }
}
