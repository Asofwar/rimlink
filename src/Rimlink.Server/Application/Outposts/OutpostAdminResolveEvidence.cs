using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

internal enum OutpostAdminBusinessSemantic { NoNewBusinessEffect = 1, AbortNoRemoval = 2, AbortWithReturn = 3, OrdinaryReturnStarted = 4 }
internal enum OutpostAdminIntentTable { OutpostOperation = 1, OrdinaryReturn = 2, CustodyTransition = 3 }
internal sealed record OutpostAdminBusinessScope(string World, long Owner, long Generation, long Epoch, string Boundary);
internal sealed record OutpostAdminBusinessOriginal(OutpostAdminOperationFamily Family, string Kind, string RequestId,
    string? LocalOperationId, byte[] RequestDigest);
internal sealed record OutpostAdminIntentLocator(OutpostAdminIntentTable Table, string Id, string Field, int Length, byte[] RawSha256);
internal sealed record OutpostAdminResolvedParcel(long Id, int Length, byte[] PayloadSha256);
internal sealed record OutpostAdminNoEffect(string SourceTransitionId, byte[] SourceTransitionDigest, int ResultLength,
    byte[] ResultSha256, OutpostOperationStatus Status, long Revision, OutpostAdminResolvedParcel[] Parcels);
internal sealed record OutpostAdminResolvedBinding(int Version, string Format, string CommandId, byte[] CommandDigest,
    OutpostAdminBusinessSemantic Semantic, string OperationId, OutpostAdminBusinessOriginal Original,
    OutpostAdminBusinessScope Scope, string ExpectedState, long Revision, OutpostAdminIntentLocator Intent,
    OutpostAdminEvidenceReference[] Prerequisites,
    OutpostAdminNoEffect? NoEffect,
    byte[] AdminResultSha256,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OutpostAdminCancellationEffect? Cancellation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OutpostAdminOrdinaryEffect? Ordinary = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OutpostAdminOrdinaryReference? Reference = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OutpostAdminOrdinaryApplied? Applied = null);
internal sealed record OutpostAdminTerminalEvidence(OutpostOperation Operation, OutpostOperationReply Reply,
    OutpostTransition Source, OutpostAdminResolvedParcel[] Parcels);
internal sealed record OutpostAdminSemanticView(OutpostTransition Raw, OutpostAdminBusinessSemantic Semantic,
    string? BusinessKind = null, byte[]? BusinessIntent = null);

/// <summary>Reads one closed semantic binding after raw chain and reciprocal command/audit validation. Never creates a virtual transition.</summary>
internal static partial class OutpostAdminResolveEvidence
{
    internal static async Task<OutpostAdminTerminalEvidence> ReadTerminalAsync(RimlinkDbContext db, Saves.SaveFiles files,
        OutpostAdminResolutionSelection selected, CancellationToken ct, IReadOnlyDictionary<string, OutpostTransition>? transitions = null)
    {
        if (selected.Kind != OutpostAdminResolutionKind.ExistingTerminal || selected.Original.Family != OutpostAdminOperationFamily.Outpost
            || selected.CanonicalSnapshot is not null || selected.Original.LocalOperationId is not null || selected.Original.DerivedRoute is not null)
            throw Invalid();
        var operation = await db.OutpostOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == selected.OperationId
            && o.Intent.Length >= 1 && o.Intent.Length <= Limits.MaxMessageBytes && o.Result.Length >= 1 && o.Result.Length <= Limits.MaxMessageBytes, ct);
        if (operation is null || operation.Kind != selected.Original.Kind || operation.RequestId != selected.Original.RequestId
            || !operation.IntentDigest.AsSpan().SequenceEqual(selected.Original.IntentDigest) || operation.State != selected.ExpectedState
            || operation.State is not ("Committed" or "Cancelled") || operation.ReservedSilver != 0
            || operation.WorldId != selected.Scope.WorldInstanceId || operation.PlayerId != selected.Scope.OwnerPlayerId
            || operation.SaveGeneration != selected.Scope.SaveGeneration || operation.SaveWriteEpoch != selected.Scope.SaveWriteEpoch
            || operation.RestoreBoundary != selected.Scope.RestoreBoundary) throw Invalid();
        OutpostOperationReply reply;
        try
        {
            reply = OutpostOperationReply.Parser.ParseFrom(operation.Result);
            if (operation.Kind is "Found" or "Assign")
            {
                var request = OutpostPrepareRequest.Parser.ParseFrom(operation.Intent);
                if (request.Kind.ToString() != operation.Kind || request.ClientRequestId != operation.RequestId || !request.Scope.Equals(selected.Scope)
                    || !OutpostRequestIdentity.Digest(request).Equals(request.IntentDigest)
                    || !request.IntentDigest.Span.SequenceEqual(operation.IntentDigest) || !request.Equals(reply.PreparedIntent)) throw Invalid();
            }
            else
            {
                var request = OutpostMutationRequest.Parser.ParseFrom(operation.Intent);
                if (request.Kind is not (OutpostMutationKind.Claim or OutpostMutationKind.Recall or OutpostMutationKind.Close or OutpostMutationKind.Switch)
                    || request.Kind.ToString() != operation.Kind || request.ClientRequestId != operation.RequestId || !request.Scope.Equals(selected.Scope)
                    || !OutpostService.Digest(request).Equals(request.IntentDigest) || !request.IntentDigest.Span.SequenceEqual(operation.IntentDigest)
                    || reply.PreparedIntent is not null || operation.State != "Committed") throw Invalid();
            }
        }
        catch (InvalidProtocolBufferException) { throw Invalid(); }
        var status = operation.State == "Committed" ? OutpostOperationStatus.Committed : OutpostOperationStatus.Cancelled;
        var source = await db.OutpostTransitions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == operation.TransitionId, ct);
        if (source is null) throw Invalid();
        string? kind = source.Kind; byte[]? businessIntent = source.Intent;
        if (source.Kind == "AdminResolved")
        {
            // Only a validated original cancellation has business semantics. A descriptive NoEffect receipt never unwraps into a grant.
            if (Read(source).Semantic is not (OutpostAdminBusinessSemantic.AbortNoRemoval or OutpostAdminBusinessSemantic.AbortWithReturn)) throw Invalid();
            transitions ??= await db.OutpostTransitions.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
            var semantic = await ReadValidatedAsync(db, files, source, transitions, ct);
            kind = semantic.BusinessKind; businessIntent = semantic.BusinessIntent;
        }
        if (kind != (operation.Kind is "Found" or "Assign" ? status == OutpostOperationStatus.Committed ? "Commit" : kind : operation.Kind)
            || status == OutpostOperationStatus.Cancelled && kind is not ("AbortNoRemoval" or "AbortWithReturn")
            || source.WorldId != operation.WorldId || source.PlayerId != operation.PlayerId || source.SaveGeneration != operation.SaveGeneration
            || source.RestoreBoundary != operation.RestoreBoundary || source.Digest.Length != 32
            || businessIntent is null || !businessIntent.AsSpan().SequenceEqual(operation.Intent) || reply.Status != status || reply.TransitionId != source.Id
            || reply.TransitionDigest.Length != 0 || !reply.Scope.Equals(selected.Scope) || reply.ClientRequestId != operation.RequestId
            || !reply.IntentDigest.Span.SequenceEqual(operation.IntentDigest) || reply.AuthoritativeRevision != selected.Revision) throw Invalid();
        var parcels = await db.OutpostParcelLinks.AsNoTracking().Where(p => p.BatchId == source.Id)
            .OrderBy(p => p.ParcelId).Take(OutpostAdminIntentCodec.MaximumReferences + 1).Select(p => new { p.ParcelId, p.PayloadDigest, Length = p.Payload.Length }).ToArrayAsync(ct);
        if (parcels.Length > OutpostAdminIntentCodec.MaximumReferences || !reply.ParcelIds.SequenceEqual(parcels.Select(p => p.ParcelId))
            || parcels.Any(p => p.PayloadDigest.Length != 32 || p.Length is < 1 or > Limits.MaxMessageBytes)) throw Invalid();
        return new(operation, reply, source, parcels.Select(p => new OutpostAdminResolvedParcel(p.ParcelId, p.Length, (byte[])p.PayloadDigest.Clone())).ToArray());
    }

    internal static byte[] Binding(OutpostAdminIntent intent, OutpostAdminCommandRecord receipt, OutpostAdminTerminalEvidence source)
    {
        var selected = intent.Resolution!;
        var binding = new OutpostAdminResolvedBinding(3, "AdminResolved-v1", receipt.Id, (byte[])receipt.CommandDigest.Clone(),
            OutpostAdminBusinessSemantic.NoNewBusinessEffect, selected.OperationId,
            new(selected.Original.Family, selected.Original.Kind, selected.Original.RequestId, selected.Original.LocalOperationId,
                (byte[])selected.Original.IntentDigest.Clone()),
            new(selected.Scope.WorldInstanceId, selected.Scope.OwnerPlayerId, selected.Scope.SaveGeneration, selected.Scope.SaveWriteEpoch, selected.Scope.RestoreBoundary),
            selected.ExpectedState, selected.Revision,
            new(OutpostAdminIntentTable.OutpostOperation, source.Operation.Id, "Intent", source.Operation.Intent.Length, SHA256.HashData(source.Operation.Intent)),
            intent.Evidence.Select(p => new OutpostAdminEvidenceReference(p.Kind, p.Id, p.Digest is null ? null : (byte[])p.Digest.Clone())).ToArray(),
            new(source.Source.Id, (byte[])source.Source.Digest.Clone(), source.Operation.Result.Length, SHA256.HashData(source.Operation.Result),
                source.Reply.Status, source.Reply.AuthoritativeRevision, source.Parcels), SHA256.HashData(receipt.Result));
        return OutpostRestoreEvidence.Encode(binding);
    }

    internal static OutpostAdminResolvedBinding Read(OutpostTransition raw)
    {
        try
        {
            if (raw.Kind != "AdminResolved" || raw.Intent.Length is < 1 or > OutpostAdminIntentCodec.MaximumCommandBytes) throw Invalid();
            var binding = OutpostRestoreEvidence.Decode<OutpostAdminResolvedBinding>(raw.Intent);
            bool reference = binding.Version == 5 && binding.Format == "AdminResolved-v3"
                && binding.Semantic == OutpostAdminBusinessSemantic.NoNewBusinessEffect && binding.Reference is not null
                && binding.NoEffect is null && binding.Cancellation is null && binding.Ordinary is null;
            bool applied = binding.Version == 6 && binding.Format == "AdminResolved-v4"
                && binding.Semantic == OutpostAdminBusinessSemantic.NoNewBusinessEffect && binding.Applied is not null
                && binding.Reference is null && binding.NoEffect is null && binding.Cancellation is null && binding.Ordinary is null;
            if (!((binding.Version == 3 && binding.Format == "AdminResolved-v1" && binding.Semantic is (OutpostAdminBusinessSemantic.NoNewBusinessEffect
                    or OutpostAdminBusinessSemantic.AbortNoRemoval or OutpostAdminBusinessSemantic.AbortWithReturn) && binding.Ordinary is null && binding.Reference is null)
                || (binding.Version == 4 && binding.Format == "AdminResolved-v2" && binding.Semantic == OutpostAdminBusinessSemantic.OrdinaryReturnStarted
                    && binding.Ordinary is not null && binding.NoEffect is null && binding.Cancellation is null && binding.Reference is null)
                || reference || applied) || !applied && binding.Applied is not null || !Enum.IsDefined(binding.Semantic)
                || binding.CommandDigest is not { Length: 32 } || !Guid.TryParseExact(binding.CommandId, "D", out var id) || id == Guid.Empty
                || binding.Original is null || binding.Scope is null || binding.Intent is null
                || !reference && !applied && binding.Semantic != OutpostAdminBusinessSemantic.OrdinaryReturnStarted && (binding.Semantic == OutpostAdminBusinessSemantic.NoNewBusinessEffect
                    ? binding.NoEffect is null || binding.Cancellation is not null : binding.NoEffect is not null || binding.Cancellation is null)
                || binding.Prerequisites is null || binding.Prerequisites.Length > OutpostAdminIntentCodec.MaximumReferences
                || !reference && !applied && binding.Semantic != OutpostAdminBusinessSemantic.OrdinaryReturnStarted
                    && (binding.NoEffect?.Parcels ?? binding.Cancellation?.Parcels) is not { Length: <= OutpostAdminIntentCodec.MaximumReferences }) throw Invalid();
            return binding;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or OutpostEvidenceUnavailableException) { throw Invalid(); }
    }

    internal static async Task<OutpostAdminSemanticView> ReadValidatedAsync(RimlinkDbContext db, Saves.SaveFiles files, OutpostTransition raw,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        if (OutpostAdminReceiptEvidence.ReadBinding(raw)?.Version == 7)
            return await ReadCanonicalSemanticAsync(db, files, raw, transitions, ct);
        var binding = Read(raw);
        var receipt = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == binding.CommandId
            && c.Command.Length <= OutpostAdminIntentCodec.MaximumCommandBytes && c.Result.Length <= OutpostAdminIntentCodec.MaximumCommandBytes, ct);
        if (receipt is null || !binding.CommandDigest.AsSpan().SequenceEqual(receipt.CommandDigest)) throw Invalid();
        var intent = OutpostAdminIntentCodec.Decode(receipt.Command);
        if (intent.Action != OutpostAdminActionKind.Resolve || receipt.TransitionId != raw.Id) throw Invalid();
        if (binding.Version == 6) return await ReadAppliedSemanticAsync(db, files, raw, intent, receipt, transitions, ct);
        if (binding.Version == 5) return await ReadOrdinaryReferenceSemanticAsync(db, files, raw, intent, receipt, transitions, ct);
        if (binding.Semantic == OutpostAdminBusinessSemantic.OrdinaryReturnStarted)
            return await ReadOrdinarySemanticAsync(db, files, raw, binding, intent, receipt, transitions, ct);
        if (intent.Resolution is not { } selected) throw Invalid();
        OutpostOperation operation; string prerequisiteId; OutpostAdminSemanticView view;
        if (binding.Semantic == OutpostAdminBusinessSemantic.NoNewBusinessEffect)
        {
            var source = await ReadTerminalAsync(db, files, selected, ct, transitions);
            if (!Binding(intent, receipt, source).AsSpan().SequenceEqual(raw.Intent)) throw Invalid();
            operation = source.Operation; prerequisiteId = source.Source.Id;
            view = new(raw, OutpostAdminBusinessSemantic.NoNewBusinessEffect);
        }
        else
        {
            var source = await ReadCancellationAsync(db, files, selected, current: false, ct);
            byte[] canonical = await CancellationBindingAsync(db, intent, receipt, source, ct);
            if (!canonical.AsSpan().SequenceEqual(raw.Intent)) throw Invalid();
            operation = source.Operation; prerequisiteId = source.Prerequisite.Id;
            view = new(raw, binding.Semantic, source.NoRemoval ? "AbortNoRemoval" : "AbortWithReturn", (byte[])operation.Intent.Clone());
        }
        // Raw locator, typed scope, prerequisites, immutable terminal descriptor and admin result must ALL match, not only their command pointer.
        var operationEvidence = intent.Evidence.SingleOrDefault(e => e.Kind == OutpostAdminEvidenceKind.Operation);
        var headEvidence = intent.Evidence.SingleOrDefault(e => e.Kind == OutpostAdminEvidenceKind.Transition);
        if (intent.Evidence.Length != 2 || operationEvidence?.Id != operation.Id || operationEvidence.Digest is not { Length: 32 }
            || !operationEvidence.Digest.AsSpan().SequenceEqual(SHA256.HashData(operation.Intent))
            || headEvidence is null || headEvidence.Id != raw.PredecessorId || headEvidence.Digest is not { Length: 32 }
            || !transitions.TryGetValue(headEvidence.Id, out var prior) || !headEvidence.Digest.AsSpan().SequenceEqual(prior.Digest)) throw Invalid();
        var cursor = raw.PredecessorId; int remaining = transitions.Count;
        while (cursor != prerequisiteId)
        {
            if (--remaining < 0 || cursor is null || !transitions.TryGetValue(cursor, out var ancestor)) throw Invalid();
            cursor = ancestor.PredecessorId;
        }
        return view;
    }

    internal static async Task<IReadOnlyDictionary<string, OutpostAdminSemanticView>> ValidateAllAsync(RimlinkDbContext db, Saves.SaveFiles files,
        IReadOnlyDictionary<string, OutpostTransition> transitions, CancellationToken ct)
    {
        var result = new Dictionary<string, OutpostAdminSemanticView>(StringComparer.Ordinal);
        foreach (var raw in transitions.Values.Where(t => t.Kind == "AdminResolved"))
            result.Add(raw.Id, await ReadValidatedAsync(db, files, raw, transitions, ct));
        return result;
    }
    private static OutpostEvidenceUnavailableException Invalid() => new("admin-resolution-evidence-mismatch");
}
