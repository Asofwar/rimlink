using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    public Task<OutpostAdminPreview> PreviewResolveAsync(Actor actor, OutpostAdminResolutionSelection selection,
        string requestId, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin); ValidatePreviewIdentity(requestId, reason);
        var selected = FreezeResolution(selection);
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed.Committed is not { DomainFormat: OutpostDomainFormat.V2 } expected)
                return new OutpostAdminPreview(null, ["admin-resolution-not-eligible"]);
            if (authority.IsRuntimeComposition && !authority.ReadinessObservation.CanResolveCustody
                && selected.Kind is OutpostAdminResolutionKind.AbortNoRemoval or OutpostAdminResolutionKind.AbortWithReturn)
                return new(null, ["outpost-readiness-unavailable"]);
            Storage.Entities.OutpostOperation source;
            try
            {
                await ValidateResolutionOwnerAsync(selected, expected, token);
                source = await authority.ReadAdminResolutionAsync(selected, token);
            }
            catch (OutpostEvidenceUnavailableException error) { return new(null, [error.Message]); }
            long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
            var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expected.TransitionId, token);
            var intent = new OutpostAdminIntent(1, actor, requestId, reason, observed,
                new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow),
                OutpostAdminActionKind.Resolve,
                [new(OutpostAdminEvidenceKind.Transition, tip.Id, (byte[])tip.Digest.Clone()),
                 new(OutpostAdminEvidenceKind.Operation, source.Id, SHA256.HashData(source.Intent))], Resolution: selected);
            var frozen = OutpostAdminIntentCodec.Freeze(intent);
            return new(new(frozen, OutpostAdminIntentCodec.Digest(frozen), false), []);
        }, ct);
    }

    public async Task<OutpostAdminCommitResult> ResolveAsync(Actor actor, OutpostAdminCommand command, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        var frozen = OutpostAdminIntentCodec.Freeze(command.Intent);
        byte[] canonical = OutpostAdminIntentCodec.Encode(frozen), digest = (byte[])command.IntentDigest.Clone();
        if (frozen.Version != 1 || frozen.Resolution is null || frozen.Actor != actor || frozen.Action != OutpostAdminActionKind.Resolve || !command.Confirmed
            || digest.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical), digest)) throw Mismatch();
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct);
        using var liveLease = await live.Hold();
        var currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners)) throw Mismatch();
        var startup = await authority.CheckHeldAsync(lease, ct);
        if (startup.State != OutpostStartupState.Ready) throw Mismatch();
        var row = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var expected = frozen.Expected.Committed!; var selected = frozen.Resolution!;
        if (expected.Installation != row.InstallationId || expected.World != row.WorldId || expected.Boundary != row.RestoreBoundary) throw Mismatch();
        await ValidateResolutionOwnerAsync(selected, expected, ct);
        string key = OutpostAdminIntentCodec.ActorKey(actor);
        var prior = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.ActorKey == key
            && r.Action == frozen.Action.ToString() && r.RequestId == frozen.RequestId, ct);
        if (prior is not null)
        {
            if (!canonical.AsSpan().SequenceEqual(prior.Command)) throw Mismatch();
            var committed = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == prior.TransitionId, ct);
            return new(OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(prior.Result),
                new(committed.NextHeadId, ByteString.CopyFrom(committed.Digest)), true);
        }
        var source = await authority.ReadAdminResolutionAsync(selected, ct);
        bool cancellation = selected.Kind is OutpostAdminResolutionKind.AbortNoRemoval or OutpostAdminResolutionKind.AbortWithReturn;
        var result = Outcome(source);
        Storage.Entities.AdminAction? published = null;
        AuthorityHead head;
        await using (var transaction = await authority.BeginAdminResolveHeldAsync(lease, frozen, result, ct))
        {
            // Re-read within this sole transaction; the stage helper cannot acquire or commit a second authority scope.
            OutpostAdminCancellationEvidence? cancelled = null;
            if (cancellation)
                cancelled = await authority.StageAdminCancellationAsync(lease, transaction, selected, frozen.Allocation.TransitionId, frozen.Allocation.At, ct);
            source = cancellation ? await db.OutpostOperations.AsNoTracking().SingleAsync(o => o.Id == selected.OperationId, ct)
                : await authority.ReadAdminResolutionAsync(selected, ct);
            result = Outcome(source);
            var staged = OutpostAdminReceiptEvidence.Create(frozen, result);
            db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit); published = staged.Audit;
            byte[] binding = cancelled is null
                ? OutpostAdminResolveEvidence.Binding(frozen, staged.Receipt, await authority.ReadAdminTerminalAsync(selected, ct))
                : await OutpostAdminResolveEvidence.CancellationBindingAsync(db, frozen, staged.Receipt, cancelled, ct);
            head = await transaction.CompleteAsync(ByteString.CopyFrom(binding), ct);
        }
        await audit.Publish(published!);

        OutpostAdminDurableResult Outcome(Storage.Entities.OutpostOperation operation) => new(1, frozen.Allocation.ReceiptId,
            OutpostAdminDurableResultKind.OutpostOutcome, cancellation ? Protocol.V1.OutpostOperationStatus.Cancelled
                : Protocol.V1.OutpostOperationReply.Parser.ParseFrom(operation.Result).Status,
            null, [new(OutpostAdminEvidenceKind.Operation, operation.Id, SHA256.HashData(operation.Result))], true);
        return new(result, head, false);
    }

    private async Task ValidateResolutionOwnerAsync(OutpostAdminResolutionSelection selected, OutpostCommittedObservation expected, CancellationToken ct)
    {
        var scope = selected.Scope;
        if (scope.WorldInstanceId != expected.World || scope.RestoreBoundary != expected.Boundary
            || !await db.Players.AsNoTracking().AnyAsync(p => p.Id == scope.OwnerPlayerId && p.SaveGeneration == scope.SaveGeneration
                && p.SaveWriteEpoch == scope.SaveWriteEpoch, ct)) throw Mismatch();
    }
    private static OutpostAdminResolutionSelection FreezeResolution(OutpostAdminResolutionSelection? selected)
    {
        if (selected is null || selected.Original is null || selected.Scope is null || selected.OperationId?.Length is not (> 0 and <= 256)
            || selected.Original.IntentDigest is not { Length: 32 } || selected.Scope.CalculateSize() > 4096
            || selected.Original.Kind?.Length is not (> 0 and <= 64) || selected.Original.RequestId?.Length is not (32 or 36)
            || selected.Original.LocalOperationId?.Length > 256 || selected.Original.DerivedRoute?.CalculateSize() > 4096
            || selected.CanonicalSnapshot?.CalculateSize() > 4096 || selected.ExpectedState?.Length is not (> 0 and <= 64)) throw Mismatch();
        return selected with { Original = selected.Original with { IntentDigest = (byte[])selected.Original.IntentDigest.Clone(),
            DerivedRoute = selected.Original.DerivedRoute?.Clone() }, Scope = selected.Scope.Clone(), CanonicalSnapshot = selected.CanonicalSnapshot?.Clone() };
    }
}
