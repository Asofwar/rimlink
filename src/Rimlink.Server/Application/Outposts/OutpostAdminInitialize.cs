using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    public async Task<OutpostAdminCommitResult> InitializeAsync(Actor actor, OutpostAdminCommand command, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        var frozen = OutpostAdminIntentCodec.Freeze(command.Intent);
        var digest = (byte[])command.IntentDigest.Clone();
        if (frozen.Actor != actor || frozen.Action != OutpostAdminActionKind.Initialize || !command.Confirmed
            || digest.Length != 32 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(OutpostAdminIntentCodec.Digest(frozen), digest))
            throw Mismatch();
        authority.RequireInitializationMain();
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct);
        using var liveLease = await live.Hold();
        var currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners)) throw Mismatch();
        // This dispatcher deliberately precedes legacy Open/reconciliation: an initial Pending is not observed NULL.
        var committed = await authority.InitializeAdminHeldAsync(lease, frozen, observation, ct);
        if (committed.Audit is not null) await audit.Publish(committed.Audit);
        return committed.Result;
    }

    public Task<OutpostAdminPreview> PreviewInitializeAsync(Actor actor, string requestId, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        ValidatePreviewIdentity(requestId, reason);
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed is not { Kind: OutpostAuthorityObservationKind.Uninitialized, Uninitialized: { } expected })
                return new OutpostAdminPreview(null, ["admin-initialize-not-eligible"]);
            long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
            var intent = new OutpostAdminIntent(1, actor, requestId, reason, observed,
                new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow),
                OutpostAdminActionKind.Initialize, [], Initialize: new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                    new(expected.Schema.ProfileId, (byte[])expected.Schema.Digest.Clone())));
            var frozen = OutpostAdminIntentCodec.Freeze(intent);
            return new(new(frozen, OutpostAdminIntentCodec.Digest(frozen), false), []);
        }, ct);
    }
}
