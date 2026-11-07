using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed partial class OutpostAdminService
{
    public Task<OutpostAdminPreview> PreviewConfirmManifestAsync(Actor actor, string manifestId, byte[] expectedDigest,
        string requestId, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin); ValidatePreviewIdentity(requestId, reason);
        if (!Guid.TryParseExact(manifestId, "D", out var id) || id == Guid.Empty || expectedDigest is not { Length: 32 }) throw Mismatch();
        byte[] digest = (byte[])expectedDigest.Clone();
        return authority.ReadAdminProjectionAsync(observation, live, async (observed, token) =>
        {
            if (observed.Committed is not { DomainFormat: OutpostDomainFormat.V2 } expected)
                return new OutpostAdminPreview(null, ["admin-manifest-not-eligible"]);
            if (authority.IsRuntimeComposition && !authority.ReadinessObservation.CanResolveCustody && await db.PawnCustody.AnyAsync(token))
                return new(null, ["outpost-readiness-unavailable"]);
            ManifestConfirmation candidate;
            try { candidate = await manifests.PrepareAdminConfirmationAsync(manifestId, digest, token); }
            catch (RpcException) { return new(null, ["admin-manifest-not-eligible"]); }
            catch (OutpostEvidenceUnavailableException error) { return new(null, [error.Message]); }
            long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
            var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expected.TransitionId, token);
            var intent = new OutpostAdminIntent(1, actor, requestId, reason, observed,
                new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow),
                OutpostAdminActionKind.ConfirmManifest,
                [new(OutpostAdminEvidenceKind.Transition, tip.Id, (byte[])tip.Digest.Clone()),
                 new(OutpostAdminEvidenceKind.Manifest, candidate.Row.Id, (byte[])candidate.Row.Digest.Clone())], Manifest: candidate.Selection);
            var frozen = OutpostAdminIntentCodec.Freeze(intent);
            return new(new(frozen, OutpostAdminIntentCodec.Digest(frozen), false), []);
        }, ct);
    }

    public async Task<OutpostAdminCommitResult> ConfirmManifestAsync(Actor actor, OutpostAdminCommand command, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        var frozen = OutpostAdminIntentCodec.Freeze(command.Intent);
        byte[] canonical = OutpostAdminIntentCodec.Encode(frozen), digest = (byte[])command.IntentDigest.Clone();
        if (frozen.Actor != actor || frozen.Action != OutpostAdminActionKind.ConfirmManifest || !command.Confirmed
            || digest.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonical), digest)) throw Mismatch();
        long[] owners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        await using var lease = await authority.AcquireAsync(owners, ct);
        using var liveLease = await live.Hold();
        var currentOwners = await db.Players.AsNoTracking().OrderBy(p => p.Id).Select(p => p.Id).ToArrayAsync(ct);
        if (!owners.SequenceEqual(currentOwners)) throw Mismatch();
        var startup = await authority.CheckHeldAsync(lease, ct);
        if (startup.State != OutpostStartupState.Ready) throw Mismatch();
        var row = await db.OutpostAuthority.AsNoTracking().SingleAsync(ct);
        var expected = frozen.Expected.Committed!;
        if (expected.Installation != row.InstallationId || expected.World != row.WorldId || expected.Boundary != row.RestoreBoundary) throw Mismatch();
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
        var selected = frozen.Manifest!;
        var result = new OutpostAdminDurableResult(1, frozen.Allocation.ReceiptId, OutpostAdminDurableResultKind.ManifestConfirmed,
            null, null, [new(OutpostAdminEvidenceKind.Manifest, selected.ManifestId, (byte[])selected.Digest.Clone())], true);
        var staged = OutpostAdminReceiptEvidence.Create(frozen, result);
        AuthorityHead head;
        await using (var transaction = await authority.BeginAdminManifestHeldAsync(lease, frozen, ct))
        {
            try { await manifests.StageAdminConfirmationAsync(lease, selected, frozen.Allocation.TransitionId, frozen.Allocation.At, ct); }
            catch (RpcException) { throw Mismatch(); }
            db.OutpostAdminCommands.Add(staged.Receipt); db.AdminActions.Add(staged.Audit);
            head = await transaction.CompleteAsync(ByteString.CopyFrom(OutpostAdminReceiptEvidence.Binding(staged.Receipt)), ct);
        }
        await audit.Publish(staged.Audit);
        return new(result, head, false);
    }
}
