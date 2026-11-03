using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.World;
using Rimlink.Server.Grpc;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Outposts;

public sealed class OutpostTileClaims(RimlinkDbContext db, OutpostAuthority authority)
{
    public async Task RequireAvailableHeldAsync(OutpostLease lease, string surfaceId, int tile, string? reservationId, CancellationToken ct)
    {
        lease.Require(authority);
        if (surfaceId != OutpostManifestService.MainSurface || tile < 0)
            throw RpcErrors.Failed(ErrorCode.OutpostManifestMismatch, RefusalKeys.OutpostsManifest, "outpost-tile-surface-invalid");
        var world = await db.World.AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw RpcErrors.Failed(ErrorCode.WorldNotCreated, RefusalKeys.WorldNotCreated, "the world has not been created yet");
        if (WorldService.NpcTilesOf(world).Contains(tile) ||
            await db.WorldObjects.AnyAsync(o => o.Tile == tile && o.Type == WorldObjectTypes.Settlement, ct) ||
            await db.Outposts.AnyAsync(o => o.WorldId == world.InfluenceInstanceId && o.Tile == tile && o.OccupiesSlot, ct))
            throw Taken();
        var reservations = await db.OutpostReservations.AsNoTracking()
            .Where(r => r.WorldId == world.InfluenceInstanceId && r.Tile == tile && r.Active).ToListAsync(ct);
        if (reservations.Any(r => r.Id != reservationId || !lease.CoversOwner(r.PlayerId))) throw Taken();
    }

    // Caller holds the common SaveFiles.PerStoreLock; legacy worlds need no authority initialization.
    public async Task<bool> IsOutpostOccupiedHeldAsync(int tile, CancellationToken ct = default)
    {
        string? world = await db.World.AsNoTracking().Select(w => w.InfluenceInstanceId).SingleOrDefaultAsync(ct);
        return world is not null && (await db.Outposts.AnyAsync(o => o.WorldId == world && o.Tile == tile && o.OccupiesSlot, ct) ||
            await db.OutpostReservations.AnyAsync(r => r.WorldId == world && r.Tile == tile && r.Active, ct));
    }

    private static global::Grpc.Core.RpcException Taken() => RpcErrors.Failed(ErrorCode.TileTaken, RefusalKeys.WorldTileTaken, "an outpost or settlement reserves this tile");
}
