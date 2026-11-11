using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    private Task<OutpostAdminPreview> PreviewCanonicalAsync(Actor actor, OutpostAdminRecoveryTarget selection,
        string requestId, string reason, CancellationToken ct)
    {
        if (selection is not { Kind: OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Ordinary: null, Canonical: {
                OriginalScope: not null, ViewScope: not null, RawDigest.Length: 32 } c }
            || c.OriginalScope.CalculateSize() > 4096 || c.ViewScope.CalculateSize() > 4096) throw Mismatch();
        var selected = c with { OriginalScope = c.OriginalScope.Clone(), ViewScope = c.ViewScope.Clone(), RawDigest = (byte[])c.RawDigest.Clone(),
            ViewRootDigest = c.ViewRootDigest?.ToArray(), ViewPathDigest = c.ViewPathDigest?.ToArray() };
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, lease, token) =>
        {
            if (observed.Committed is not { DomainFormat: OutpostDomainFormat.V2 or OutpostDomainFormat.V3 } expected || lease is null)
                return new OutpostAdminPreview(null, ["admin-resolution-not-eligible"]);
            try
            {
                await ValidateRecoveryOwnerAsync(selected.ViewScope, expected, token);
                await authority.ReadAdminCanonicalCurrentAsync(lease, selected, token);
                long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
                if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
                var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expected.TransitionId, token);
                var allocation = new OutpostAdminAllocation(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow);
                var frozen = OutpostAdminIntentCodec.Freeze(new(2, actor, requestId, reason, observed, allocation, OutpostAdminActionKind.Resolve,
                    OutpostAdminResolveEvidence.CanonicalPrerequisites(tip, selected), Recovery: new(OutpostAdminRecoveryTargetKind.CanonicalSnapshot, Canonical: selected)));
                return new(new(frozen, OutpostAdminIntentCodec.Digest(frozen), false), []);
            }
            catch (Exception error) when (error is OutpostEvidenceUnavailableException or InvalidDataException or IOException)
            { return new(null, ["admin-canonical-evidence-not-eligible"]); }
        }, ct);
    }

    private async Task<OutpostAdminCommitResult> ResolveCanonicalAsync(Actor actor, OutpostAdminIntent frozen,
        byte[] digest, bool confirmed, CancellationToken ct)
    {
        byte[] canonical = OutpostAdminIntentCodec.Encode(frozen);
        if (frozen.Actor != actor || !confirmed || frozen.Action != OutpostAdminActionKind.Resolve || digest.Length != 32
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical), digest)) throw Mismatch();
        var selected = frozen.Recovery!.Canonical!;
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct); using var liveLease = await live.Hold();
        var currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners) || (await authority.CheckHeldAsync(lease, ct)).State != OutpostStartupState.Ready) throw Mismatch();
        var row = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct); var expected = frozen.Expected.Committed!;
        if (expected.Installation != row.InstallationId || expected.World != row.WorldId || expected.Boundary != row.RestoreBoundary) throw Mismatch();
        await ValidateRecoveryOwnerAsync(selected.ViewScope, expected, ct);
        string key = OutpostAdminIntentCodec.ActorKey(actor);
        var prior = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.ActorKey == key && r.Action == frozen.Action.ToString()
            && r.RequestId == frozen.RequestId, ct);
        if (prior is not null)
        {
            if (!canonical.AsSpan().SequenceEqual(prior.Command)) throw Mismatch();
            var committed = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == prior.TransitionId, ct);
            return new(OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(prior.Result), new(committed.NextHeadId, ByteString.CopyFrom(committed.Digest)), true);
        }
        if (expected.HeadId != row.HeadId || expected.TransitionId != row.TransitionId || !expected.HeadDigest.AsSpan().SequenceEqual(row.HeadDigest)) throw Mismatch();
        var result = new OutpostAdminDurableResult(1, frozen.Allocation.ReceiptId, OutpostAdminDurableResultKind.CanonicalDownloadRequired,
            null, null, [new(OutpostAdminEvidenceKind.Snapshot, selected.SnapshotId, selected.RawDigest.ToArray())], true);
        var staged = OutpostAdminReceiptEvidence.Create(frozen, result); AuthorityHead head;
        await using (var transaction = await authority.BeginAdminResolveHeldAsync(lease, frozen, result, ct))
        {
            await authority.ReadAdminCanonicalCurrentAsync(lease, selected, ct);
            db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit);
            head = await transaction.CompleteAsync(ByteString.CopyFrom(OutpostAdminResolveEvidence.CanonicalBinding(frozen, staged.Receipt)), ct);
        }
        await audit.Publish(staged.Audit); return new(result, head, false);
    }
}
