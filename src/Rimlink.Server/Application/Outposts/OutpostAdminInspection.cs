using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Outposts;

public sealed record OutpostAdminPreview(OutpostAdminCommand? Command, string[] Blockers);

public sealed partial class OutpostAdminService
{
    public Task<OutpostAuthorityObservation> InspectAsync(Actor actor, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        return authority.ReadAdminProjectionAsync(observation, live, (observation, _) => Task.FromResult(observation), ct);
    }

    public Task<OutpostAdminPreview> PreviewActivateDomainFormatAsync(Actor actor, string requestId, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        ValidatePreviewIdentity(requestId, reason);
        return authority.ReadAdminProjectionAsync(observation, live, async (observation, token) =>
        {
            if (observation.Committed is not { DomainFormat: OutpostDomainFormat.V1 } expected)
                return new OutpostAdminPreview(null, ["admin-domain-activation-not-eligible"]);
            var tip = await db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == expected.TransitionId);
            long maximum = await db.AdminActions.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            if (maximum == long.MaxValue) return new(null, ["admin-audit-allocation-unavailable"]);
            var schema = new OutpostSchemaBinding(expected.Schema.ProfileId, (byte[])expected.Schema.Digest.Clone());
            var intent = new OutpostAdminIntent(1, actor, requestId, reason, observation,
                new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), maximum + 1, DateTime.UtcNow),
                OutpostAdminActionKind.ActivateDomainFormat,
                [new(OutpostAdminEvidenceKind.Transition, tip.Id, (byte[])tip.Digest.Clone())],
                Activation: new(OutpostDomainFormat.V1, OutpostDomainFormat.V2, (byte[])tip.Effects.Clone(), schema, schema, OutpostDomainSchema.AdminMigrationId));
            var frozen = OutpostAdminIntentCodec.Freeze(intent);
            return new(new(frozen, SHA256.HashData(OutpostAdminIntentCodec.Encode(frozen)), false), []);
        }, ct);
    }

    private static void ValidatePreviewIdentity(string requestId, string reason)
    {
        if (requestId is null || requestId.Length != 36 || !Guid.TryParseExact(requestId, "D", out var parsed) || parsed == Guid.Empty
            || string.IsNullOrWhiteSpace(reason) || reason.Any(char.IsControl)) throw new InvalidDataException("outpost-admin-intent-invalid");
        try { if (new UTF8Encoding(false, true).GetByteCount(reason) > 1024) throw new InvalidDataException("outpost-admin-intent-invalid"); }
        catch (EncoderFallbackException error) { throw new InvalidDataException("outpost-admin-intent-invalid", error); }
    }
}
