using Microsoft.EntityFrameworkCore;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Colonies;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Rest;

public static class ColonyEndpoints
{
    public static void MapColonyApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/colonies").RequireAdmin();
        group.MapGet("/", async (RimlinkDbContext db, long? playerId) =>
            await db.Set<ColonyMember>().AsNoTracking().Where(x => playerId == null || x.OwnerPlayerId == playerId || x.MemberPlayerId == playerId)
                .OrderBy(x => x.OwnerPlayerId).ThenBy(x => x.MemberPlayerId).Take(200)
                .Select(x => new { x.OwnerPlayerId, x.MemberPlayerId, x.Accepted, x.PawnIds }).ToListAsync()).RequireScope(ApiScope.Moderate);
        group.MapDelete("/{owner:long}/{member:long}", async (long owner, long member, ColonyService colonies, HttpContext ctx, AdminAudit audit) =>
        {
            await colonies.Revoke(owner, owner, member);
            await audit.Write(ctx.Actor(), "colony-revoke", null, $"owner {owner}, member {member}");
            return Results.Ok();
        }).RequireScope(ApiScope.Admin);
    }
}
