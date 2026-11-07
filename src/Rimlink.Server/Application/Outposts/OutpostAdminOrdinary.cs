using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    public Task<OutpostAdminPreview> PreviewResolveAsync(Actor actor, OutpostAdminRecoveryTarget selection,
        OriginalOrdinaryInput? originalInput, string requestId, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin); ValidatePreviewIdentity(requestId, reason);
        if (selection?.Kind == OutpostAdminRecoveryTargetKind.CanonicalSnapshot)
        {
            if (originalInput is not null) throw Mismatch();
            return PreviewCanonicalAsync(actor, selection, requestId, reason, ct);
        }
        var target = FreezeOrdinaryTarget(selection);
        var input = originalInput is null ? null : OutpostAdminIntentCodec.FreezeOrdinaryInput(originalInput);
        if (target.Ordinary!.ExpectedState is "Applied" or "ReturnStarted" or "ReturnAccepted")
            return PreviewOrdinaryReferenceAsync(actor, target, input, requestId, reason, ct);
        if (input is null) return Task.FromResult(new OutpostAdminPreview(null, ["admin-original-ordinary-input-required"]));
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed.Committed is not { DomainFormat: OutpostDomainFormat.V2 } expected || target.Ordinary!.Input is not null)
                return new OutpostAdminPreview(null, ["admin-resolution-not-eligible"]);
            if (authority.IsRuntimeComposition && !authority.ReadinessObservation.CanResolveCustody)
                return new(null, ["outpost-readiness-unavailable"]);
            long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
            var allocation = new OutpostAdminAllocation(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow);
            var selected = target.Ordinary with { Input = OutpostAdminIntentCodec.CommitOrdinaryInput(input, allocation.TransitionId) };
            try
            {
                await ValidateRecoveryOwnerAsync(selected.Scope, expected, token);
                var candidate = await authority.ReadNewAdminOrdinaryAsync(selected, input, allocation.TransitionId, token);
                var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expected.TransitionId, token);
                var intent = new OutpostAdminIntent(2, actor, requestId, reason, observed, allocation, OutpostAdminActionKind.Resolve,
                    OutpostAdminResolveEvidence.OrdinaryPrerequisites(tip, selected, candidate.Capture), Recovery: target with { Ordinary = selected });
                var frozen = OutpostAdminIntentCodec.Freeze(intent);
                return new(new(frozen, OutpostAdminIntentCodec.Digest(frozen), false), []);
            }
            catch (Exception error) when (error is OutpostEvidenceUnavailableException or RpcException) { return new(null, ["admin-ordinary-evidence-not-eligible"]); }
        }, ct);
    }

    public async Task<OutpostAdminCommitResult> ResolveAsync(Actor actor, OutpostAdminCommand command,
        OriginalOrdinaryInput? originalInput, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        var frozen = OutpostAdminIntentCodec.Freeze(command.Intent);
        byte[] canonical = OutpostAdminIntentCodec.Encode(frozen), digest = (byte[])command.IntentDigest.Clone();
        var input = originalInput is null ? null : OutpostAdminIntentCodec.FreezeOrdinaryInput(originalInput);
        if (frozen.Version == 2 && frozen.Recovery?.Kind == OutpostAdminRecoveryTargetKind.CanonicalSnapshot)
        {
            if (input is not null) throw Mismatch();
            return await ResolveCanonicalAsync(actor, frozen, digest, command.Confirmed, ct);
        }
        if (frozen.Version == 2 && frozen.Recovery?.Ordinary is { Input: null, ExpectedState: "Applied" or "ReturnStarted" or "ReturnAccepted" })
            return await ResolveOrdinaryReferenceAsync(actor, frozen, digest, command.Confirmed, input, ct);
        if (frozen.Version != 2 || frozen.Recovery?.Kind != OutpostAdminRecoveryTargetKind.OrdinaryCapture
            || frozen.Recovery.Ordinary is not { Input: not null } selected || frozen.Actor != actor || !command.Confirmed
            || frozen.Action != OutpostAdminActionKind.Resolve || digest.Length != 32
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical), digest)) throw Mismatch();
        if (input is not null && !OutpostAdminResolveEvidence.SameCommitment(selected.Input,
            OutpostAdminIntentCodec.CommitOrdinaryInput(input, frozen.Allocation.TransitionId))) throw Mismatch();
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct); using var liveLease = await live.Hold();
        var currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners) || (await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready) throw Mismatch();
        var row = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct); var expected = frozen.Expected.Committed!;
        if (expected.Installation != row.InstallationId || expected.World != row.WorldId || expected.Boundary != row.RestoreBoundary) throw Mismatch();
        await ValidateRecoveryOwnerAsync(selected.Scope, expected, ct);
        string key = OutpostAdminIntentCodec.ActorKey(actor);
        var prior = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.ActorKey == key && r.Action == frozen.Action.ToString()
            && r.RequestId == frozen.RequestId, ct);
        if (prior is not null)
        {
            if (!canonical.AsSpan().SequenceEqual(prior.Command)) throw Mismatch();
            var committed = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == prior.TransitionId, ct);
            return new(OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(prior.Result), new(committed.NextHeadId, ByteString.CopyFrom(committed.Digest)), true);
        }
        if (input is null || expected.HeadId != row.HeadId || expected.TransitionId != row.TransitionId
            || !expected.HeadDigest.AsSpan().SequenceEqual(row.HeadDigest)) throw Mismatch();
        var result = new OutpostAdminDurableResult(1, frozen.Allocation.ReceiptId, OutpostAdminDurableResultKind.OrdinaryOutcome, null,
            OrdinaryOperationStatus.ReturnStarted, [new(OutpostAdminEvidenceKind.Operation, frozen.Allocation.TransitionId, selected.Input.AuthorizationDigest)], true);
        var staged = OutpostAdminReceiptEvidence.Create(frozen, result); AuthorityHead head;
        await using (var transaction = await authority.BeginAdminResolveHeldAsync(lease, frozen, result, ct))
        {
            var returned = await authority.StageAdminOrdinaryAsync(lease, transaction, frozen, input, ct);
            db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit);
            head = await transaction.CompleteAsync(ByteString.CopyFrom(OutpostAdminResolveEvidence.OrdinaryBinding(frozen, staged.Receipt, returned)), ct);
        }
        await audit.Publish(staged.Audit); return new(result, head, false);
    }

    private async Task ValidateRecoveryOwnerAsync(OutpostScopeMessage scope, OutpostCommittedObservation expected, CancellationToken ct)
    {
        if (scope.WorldInstanceId != expected.World || scope.RestoreBoundary != expected.Boundary
            || !await db.Players.AsNoTracking().AnyAsync(p => p.Id == scope.OwnerPlayerId && p.SaveGeneration == scope.SaveGeneration
                && p.SaveWriteEpoch == scope.SaveWriteEpoch, ct)) throw Mismatch();
    }

    private static OutpostAdminRecoveryTarget FreezeOrdinaryTarget(OutpostAdminRecoveryTarget? target)
    {
        if (target is not { Kind: OutpostAdminRecoveryTargetKind.OrdinaryCapture, Ordinary: { Scope: not null, Original: not null,
                RemovalSnapshot: not null, CanonicalSnapshot: not null } s, Canonical: null }
            || s.Original.IntentDigest is not { Length: 32 } || s.Scope.CalculateSize() > 4096
            || s.RemovalSnapshot.CalculateSize() > 4096 || s.CanonicalSnapshot.CalculateSize() > 4096) throw Mismatch();
        return target with { Ordinary = s with { Original = s.Original with { IntentDigest = (byte[])s.Original.IntentDigest.Clone(),
            DerivedRoute = s.Original.DerivedRoute?.Clone() }, Scope = s.Scope.Clone(), RemovalSnapshot = s.RemovalSnapshot.Clone(),
            CanonicalSnapshot = s.CanonicalSnapshot.Clone(), Input = s.Input is null ? null : s.Input with { InputDigest = (byte[])s.Input.InputDigest.Clone(),
                OriginalDigest = (byte[])s.Input.OriginalDigest.Clone(), AuthorizationDigest = (byte[])s.Input.AuthorizationDigest.Clone() } } };
    }
}
