using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Storage.Entities;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.GuildCapture;

internal sealed record GuildPlacementCommand(GuildMutationScope Scope, string RequestId, int Tile, GuildResource Resource,
    string Reason, string ExpectedHeadId);
internal sealed record GuildPlacementPreview(GuildPlacementCommand Command, string IntentDigest);
internal sealed record GuildReviewedPlacementReceipt(GuildPlacementCommand Command, GuildOutpostResult Result);

internal sealed partial class GuildOutpostService
{
    internal Task<GuildPlacementPreview> PreviewPlacementAsync(Actor actor, OutpostMainObservation observation,
        int tile, GuildResource resource, string reason, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        ValidatePlacement(tile, resource, reason);
        return authority.ReadAdminProjectionAsync<GuildPlacementPreview>(observation, live, async (observed, lease, token) =>
        {
            if (lease is null || observed.Committed is not { DomainFormat: OutpostDomainFormat.V3 } current)
                throw new OutpostEvidenceUnavailableException("guild-placement-observation-unavailable");
            authority.RequireRuntimeGuild(GuildRuntimeUse.Setup);
            var scope = new GuildMutationScope(current.World, current.Boundary);
            await RequirePlacementTileAsync(lease, scope, tile, resource, token);
            var command = new GuildPlacementCommand(scope, Guid.NewGuid().ToString("D"), tile, resource, reason, current.HeadId);
            return new(command, PlacementDigest(actor, command));
        }, ct);
    }

    internal async Task<GuildOutpostResult> CommitPlacementAsync(Actor actor, GuildPlacementCommand command,
        string intentDigest, bool confirmed, CancellationToken ct)
    {
        actor.Require(Role.Admin);
        ValidatePlacement(command.Tile, command.Resource, command.Reason);
        if (!confirmed || !Guid.TryParseExact(command.ExpectedHeadId, "D", out _)
            || intentDigest != PlacementDigest(actor, command)) throw Refused();
        var receipt = await MutateAsync(command.Scope, actor.PlayerId, actor.Name, command.RequestId, "GuildPlacement",
            new { actor, command }, async (lease, id, token) =>
            {
                if (!await db.OutpostAuthority.AsNoTracking().AnyAsync(a => a.HeadId == command.ExpectedHeadId, token))
                    throw new OutpostEvidenceUnavailableException("guild-placement-preview-stale");
                var result = await StagePlacementAsync(lease, command.Scope, command.Tile, command.Resource, id, token);
                return new GuildReviewedPlacementReceipt(command, result);
            }, ct);
        return receipt.Result;
    }

    private static string PlacementDigest(Actor actor, GuildPlacementCommand command) =>
        Convert.ToHexStringLower(GuildAuthorityEvidence.IntentDigest(new { actor, command }));

    private static void ValidatePlacement(int tile, GuildResource resource, string reason)
    {
        if (tile < 0 || resource is not (GuildResource.Steel or GuildResource.Wood) || string.IsNullOrWhiteSpace(reason)
            || reason.Length > 500 || reason.Any(char.IsControl)) throw Refused();
    }

    private async Task RequirePlacementTileAsync(OutpostLease lease, GuildMutationScope scope, int tile, GuildResource resource, CancellationToken ct)
    {
        if (await db.Set<GuildOutpostRow>().CountAsync(o => o.WorldId == scope.WorldId, ct) >= 32) throw Refused();
        await manifests.RequireActiveHeldAsync(lease, OutpostManifestService.MainSurface, tile,
            resource == GuildResource.Steel ? OutpostProfile.Steel : OutpostProfile.Wood, ct);
        await tiles.RequireAvailableHeldAsync(lease, OutpostManifestService.MainSurface, tile, null, ct);
    }

    private async Task<GuildOutpostResult> StagePlacementAsync(OutpostLease lease, GuildMutationScope scope, int tile,
        GuildResource resource, string id, CancellationToken ct)
    {
        await RequirePlacementTileAsync(lease, scope, tile, resource, ct);
        var row = new GuildOutpostRow { Id = Guid.NewGuid().ToString("D"), WorldId = scope.WorldId, Tile = tile, Resource = resource };
        Checkpoint(row, clock.Sample());
        db.Add(row);
        return Result(scope, row, id);
    }
}
