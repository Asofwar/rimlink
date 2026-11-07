using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Storage;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostAdminCommitResult(OutpostAdminDurableResult Outcome, AuthorityHead Head, bool Replay);

public sealed partial class OutpostAdminService(RimlinkDbContext db, OutpostAuthority authority, LiveStateLock live, AdminAudit audit, OutpostMainObservation observation, OutpostManifestService manifests)
{
    public async Task<OutpostAdminCommitResult> ActivateDomainFormatAsync(Actor actor, OutpostAdminCommand command, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        // Detach every caller-owned buffer before the first await; body fields never authenticate their actor.
        var frozen = OutpostAdminIntentCodec.Freeze(command.Intent);
        byte[] canonical = OutpostAdminIntentCodec.Encode(frozen);
        byte[] suppliedDigest = (byte[])command.IntentDigest.Clone();
        if (frozen.Actor != actor || frozen.Action != OutpostAdminActionKind.ActivateDomainFormat || !command.Confirmed
            || suppliedDigest.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical), suppliedDigest)) throw Mismatch();
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct);
        using var liveLease = await live.Hold();
        var currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners)) throw Mismatch();
        var startup = await authority.CheckHeldAsync(lease, ct);
        if (startup.State is not (OutpostStartupState.Ready or OutpostStartupState.NoActiveWorld)) throw Mismatch();
        var row = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var expected = frozen.Expected.Committed!;
        // Replay retains its original head, but is not a grant in a retired installation/world/boundary.
        if (expected.Installation != row.InstallationId || expected.World != row.WorldId || expected.Boundary != row.RestoreBoundary) throw Mismatch();
        string actorKey = OutpostAdminIntentCodec.ActorKey(actor);
        var prior = await db.OutpostAdminCommands.AsNoTracking().SingleOrDefaultAsync(r => r.ActorKey == actorKey
            && r.Action == frozen.Action.ToString() && r.RequestId == frozen.RequestId, ct);
        if (prior is not null)
        {
            if (!canonical.AsSpan().SequenceEqual(prior.Command)) throw Mismatch();
            var committed = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == prior.TransitionId, ct);
            return new(OutpostRestoreEvidence.Decode<OutpostAdminDurableResult>(prior.Result),
                new(committed.NextHeadId, ByteString.CopyFrom(committed.Digest)), true);
        }
        var result = new OutpostAdminDurableResult(1, frozen.Allocation.ReceiptId,
            OutpostAdminDurableResultKind.DomainFormatActivated, null, null, [], true);
        var staged = OutpostAdminReceiptEvidence.Create(frozen, result);
        AuthorityHead head;
        await using (var transaction = await authority.BeginAdminActivationHeldAsync(lease, frozen, ct))
        {
            db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit);
            head = await transaction.CompleteAsync(ByteString.CopyFrom(OutpostAdminReceiptEvidence.Binding(staged.Receipt)), ct);
        }
        // A DB-only receipt or an uncertain external completion never reaches publication.
        await audit.Publish(staged.Audit);
        return new(result, head, false);
    }

    private static OutpostEvidenceUnavailableException Mismatch() => new("admin-intent-or-observation-mismatch");
}
