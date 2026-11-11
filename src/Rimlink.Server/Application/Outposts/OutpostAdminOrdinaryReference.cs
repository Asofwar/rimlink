using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    private Task<OutpostAdminPreview> PreviewOrdinaryReferenceAsync(Actor actor, OutpostAdminRecoveryTarget target,
        OriginalOrdinaryInput? input, string requestId, string reason, CancellationToken ct) =>
        authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed.Committed is not { DomainFormat: OutpostDomainFormat.V2 or OutpostDomainFormat.V3 } expected || target.Ordinary is not { Input: null } selected)
                return new OutpostAdminPreview(null, ["admin-resolution-not-eligible"]);
            try
            {
                await ValidateRecoveryOwnerAsync(selected.Scope, expected, token);
                var applied = selected.ExpectedState == "Applied" ? await authority.ReadAdminOrdinaryAppliedAsync(selected, token) : null;
                var source = applied is null ? await authority.ReadAdminOrdinaryReferenceAsync(selected, historical: false, token) : null;
                if (applied is not null) OutpostAdminResolveEvidence.ValidateAppliedInput(selected, applied.Capture, input);
                else OutpostAdminResolveEvidence.ValidateReferenceInput(source!, input);
                long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
                if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
                var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expected.TransitionId, token);
                var allocation = new OutpostAdminAllocation(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow);
                var intent = new OutpostAdminIntent(2, actor, requestId, reason, observed, allocation, OutpostAdminActionKind.Resolve,
                    applied is not null ? OutpostAdminResolveEvidence.AppliedPrerequisites(tip, selected, applied)
                        : OutpostAdminResolveEvidence.OrdinaryReferencePrerequisites(tip, selected, source!), Recovery: target);
                var frozen = OutpostAdminIntentCodec.Freeze(intent);
                return new(new(frozen, OutpostAdminIntentCodec.Digest(frozen), false), []);
            }
            catch (Exception error) when (error is OutpostEvidenceUnavailableException or RpcException) { return new(null, ["admin-ordinary-evidence-not-eligible"]); }
        }, ct);

    private async Task<OutpostAdminCommitResult> ResolveOrdinaryReferenceAsync(Actor actor, OutpostAdminIntent frozen, byte[] digest,
        bool confirmed, OriginalOrdinaryInput? input, CancellationToken ct)
    {
        byte[] canonical = OutpostAdminIntentCodec.Encode(frozen);
        if (frozen.Actor != actor || !confirmed || frozen.Action != OutpostAdminActionKind.Resolve || digest.Length != 32
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical), digest)) throw Mismatch();
        var selected = frozen.Recovery!.Ordinary!;
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
            if (input is not null)
            {
                if (selected.ExpectedState == "Applied") OutpostAdminResolveEvidence.ValidateAppliedInput(selected,
                    (await authority.ReadAdminOrdinaryAppliedAsync(selected, ct)).Capture, input);
                else OutpostAdminResolveEvidence.ValidateReferenceInput(await authority.ReadAdminOrdinaryReferenceAsync(selected, historical: true, ct), input);
            }
            var committed = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == prior.TransitionId, ct);
            return new(OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(prior.Result), new(committed.NextHeadId, ByteString.CopyFrom(committed.Digest)), true);
        }
        if (expected.HeadId != row.HeadId || expected.TransitionId != row.TransitionId || !expected.HeadDigest.AsSpan().SequenceEqual(row.HeadDigest)) throw Mismatch();
        var applied = selected.ExpectedState == "Applied" ? await authority.ReadAdminOrdinaryAppliedAsync(selected, ct) : null;
        var source = applied is null ? await authority.ReadAdminOrdinaryReferenceAsync(selected, historical: false, ct) : null;
        if (applied is not null) OutpostAdminResolveEvidence.ValidateAppliedInput(selected, applied.Capture, input);
        else OutpostAdminResolveEvidence.ValidateReferenceInput(source!, input);
        var result = new OutpostAdminDurableResult(1, frozen.Allocation.ReceiptId, OutpostAdminDurableResultKind.OrdinaryOutcome, null,
            applied is not null ? OrdinaryOperationStatus.Applied : selected.ExpectedState == "ReturnStarted" ? OrdinaryOperationStatus.ReturnStarted : OrdinaryOperationStatus.ReturnAccepted,
            [applied is not null ? new(OutpostAdminEvidenceKind.Transition, applied.Source.Id, (byte[])applied.Source.Digest.Clone())
                : new(OutpostAdminEvidenceKind.Operation, source!.Row.Id, SHA256.HashData(source.Row.Intent))], true);
        var staged = OutpostAdminReceiptEvidence.Create(frozen, result); AuthorityHead head;
        await using (var transaction = await authority.BeginAdminResolveHeldAsync(lease, frozen, result, ct))
        {
            // No business stage: reference only the existing immutable authorization, never allocate another attempt.
            if (applied is not null)
            {
                applied = await authority.ReadAdminOrdinaryAppliedAsync(selected, ct);
                OutpostAdminResolveEvidence.ValidateAppliedInput(selected, applied.Capture, input);
            }
            else
            {
                source = await authority.ReadAdminOrdinaryReferenceAsync(selected, historical: false, ct);
                OutpostAdminResolveEvidence.ValidateReferenceInput(source, input);
            }
            db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit);
            head = await transaction.CompleteAsync(ByteString.CopyFrom(applied is not null
                ? OutpostAdminResolveEvidence.AppliedBinding(frozen, staged.Receipt, applied)
                : OutpostAdminResolveEvidence.OrdinaryReferenceBinding(frozen, staged.Receipt, source!)), ct);
        }
        await audit.Publish(staged.Audit); return new(result, head, false);
    }
}
