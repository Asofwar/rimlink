using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Hosting;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Rest;

/// <summary>REST admin API under /api/admin, authorized by the Admin:ApiToken bearer token. Used by the CLI and the future web panel.</summary>
public static class AdminEndpoints
{
    public sealed record BanBody(long? PlayerId, long? AccountId, long? SteamId, string? Ip, string Reason, double? Days);
    public sealed record KickBody(string? Reason);
    public sealed record MuteBody(double Minutes);
    public sealed record TextBody(string Text);
    public sealed record ShutdownBody(int InSeconds, string? Reason);
    public sealed record ModpackEntryBody(int Position, string PackageId, string? Version, ulong WorkshopId, string? Url, [property: JsonConverter(typeof(JsonStringEnumConverter))] EntryKind Kind);
    public sealed record ModpackBody(List<ModpackEntryBody> Entries);
    public sealed record DefsBody(List<string> DefNames);
    public sealed record WalletBody(long Delta, string? Reason);
    public sealed record FreezeBody(bool Frozen, string? Reason);
    public sealed record ReasonBody(string? Reason);
    public sealed record CancelRaidBody(string? Reason, bool ReturnPawns = false);
    public sealed record PeacefulBody(bool Peaceful);

    public static void MapAdminApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").AddEndpointFilter(RequireToken);

        group.MapGet("/players/online", (AdminOperations ops) => ops.ListOnline(Actor.Api));
        group.MapPost("/players/{id:long}/kick", (long id, KickBody body, AdminOperations ops) => ops.Kick(Actor.Api, id, body.Reason ?? "kicked by an admin"));
        group.MapPost("/players/{id:long}/mute", (long id, MuteBody body, AdminOperations ops) => ops.Mute(Actor.Api, id, TimeSpan.FromMinutes(body.Minutes)));
        group.MapDelete("/players/{id:long}/mute", (long id, AdminOperations ops) => ops.Unmute(Actor.Api, id));
        group.MapGet("/players/{id:long}/saves", async (long id, AdminOperations ops) => (await ops.ListSaves(Actor.Api, id)).Select(SaveStore.ToInfo).Select(s => new { s.Version, s.Size, s.GameTick, s.IsCurrent, CreatedAt = s.CreatedAt.ToDateTime() }));
        group.MapPost("/players/{id:long}/saves/{version:int}/rollback", async (long id, int version, AdminOperations ops) =>
            await ops.RollbackSave(Actor.Api, id, version) ? Results.Ok() : Results.NotFound());
        group.MapPost("/players/{id:long}/reset", (long id, AdminOperations ops) => ops.ResetPlayer(Actor.Api, id));

        group.MapGet("/bans", async (AdminOperations ops) => (await ops.ListBans(Actor.Api)).Select(b => new { b.Id, b.AccountId, b.SteamId, b.Ip, b.Reason, b.ExpiresAt, b.CreatedAt }));
        group.MapPost("/bans", async (BanBody body, AdminOperations ops) =>
        {
            long? playerId = body.PlayerId;
            long? accountId = body.AccountId;
            if (playerId is null && accountId is { } acc) playerId = (await ops.FindPlayerByAccount(acc)).Id;
            var ban = await ops.Ban(Actor.Api, playerId, body.SteamId, body.Ip, body.Reason, body.Days is { } d ? TimeSpan.FromDays(d) : null);
            return Results.Ok(new { ban.Id });
        });
        group.MapDelete("/bans/{id:long}", async (long id, AdminOperations ops) => await ops.Pardon(Actor.Api, id) ? Results.Ok() : Results.NotFound());

        group.MapPost("/broadcast", (TextBody body, AdminOperations ops) => ops.Broadcast(Actor.Api, body.Text));
        group.MapPost("/shutdown", (ShutdownBody body, AdminOperations ops) => ops.Shutdown(Actor.Api, body.InSeconds, body.Reason ?? "server shutdown"));
        group.MapPost("/world/recreate", (AdminOperations ops) => ops.RecreateWorld(Actor.Api));
        group.MapGet("/actions", async (AdminOperations ops, [FromQuery] int limit = 100) => (await ops.ListActions(Actor.Api, limit)).Select(a => new { a.Id, a.At, a.ActorAccountId, a.Action, a.TargetAccountId, a.Details }));

        group.MapGet("/modpack", async (ModpackService modpack) => new ModpackBody((await modpack.Get()).Select(e => new ModpackEntryBody(e.Position, e.PackageId, e.Version, e.WorkshopId, e.Url, e.Kind)).ToList()));
        group.MapPut("/modpack/defs", async (DefsBody body, ModpackService modpack, ModpackDefs defs) =>
        {
            await modpack.SetDefs(body.DefNames, Role.Admin, "api", defs);
            return Results.Ok(new { count = defs.Count });
        });

        group.MapPost("/economy/wallet/{id:long}", async (long id, WalletBody body, AdminOperations ops) =>
            Results.Ok(new { balance = await ops.AdjustWallet(Actor.Api, id, body.Delta, body.Reason ?? "admin adjustment") }));
        group.MapGet("/economy/parcels/{id:long}", async (long id, AdminOperations ops) =>
            (await ops.ListParcels(Actor.Api, id)).Select(p => new { p.Id, Kind = p.Kind.ToString(), p.FromPlayerId, p.Silver, p.SizeBytes, p.CreatedAt, p.DeliveredAt, p.DeliveredTick }));
        group.MapGet("/economy/lots", async (AdminOperations ops, [FromQuery] long? playerId = null) =>
            (await ops.ListLots(Actor.Api, playerId)).Select(l => new { l.Id, l.SellerPlayerId, l.Price, l.CreatedAt, l.ExpiresAt, Status = l.Status.ToString() }));
        group.MapPost("/economy/refund/lot/{id:long}", async (long id, AdminOperations ops) => await ops.RefundLot(Actor.Api, id) ? Results.Ok() : Results.NotFound());
        group.MapPost("/economy/refund/parcel/{id:long}", async (long id, AdminOperations ops) => await ops.RefundParcel(Actor.Api, id) ? Results.Ok() : Results.NotFound());
        group.MapPost("/economy/freeze", async (FreezeBody body, AdminOperations ops) => { await ops.SetEconomyFrozen(Actor.Api, body.Frozen, body.Reason); return Results.Ok(); });
        group.MapGet("/economy/stats", async (AdminOperations ops) =>
        {
            var s = await ops.EconomyStats(Actor.Api);
            return new { silverInWallets = s.SilverInWallets, openLots = s.OpenLots, pendingParcels = s.PendingParcels, tradesToday = s.TradesToday, frozen = s.Frozen, freezeReason = s.FreezeReason };
        });

        group.MapGet("/raids", async (AdminOperations ops, [FromQuery] long? playerId = null) =>
            (await ops.ListRaids(Actor.Api, playerId)).Select(r => new { r.Id, r.AttackerPlayerId, r.TargetPlayerId, State = r.State.ToString(), PawnMode = ((PawnMode)r.PawnMode).ToString(), Timing = ((RaidTiming)r.Timing).ToString(), r.LootCap, r.CreatedAt, r.LandAtTick, r.LandedTick, r.ResolvedAt, r.ExpiresAt }));
        group.MapPost("/raids/{id:long}/cancel", async (long id, CancelRaidBody body, AdminOperations ops) =>
            await ops.CancelRaid(Actor.Api, id, body.Reason ?? "cancelled by admin", body.ReturnPawns) ? Results.Ok() : Results.NotFound());
        group.MapPost("/raids/freeze", async (FreezeBody body, AdminOperations ops) => { await ops.SetRaidsFrozen(Actor.Api, body.Frozen, body.Reason); return Results.Ok(); });
        group.MapGet("/raids/stats", async (AdminOperations ops) =>
        {
            var s = await ops.RaidStats(Actor.Api);
            return new { queued = s.Queued, announced = s.Announced, landed = s.Landed, resolvedToday = s.ResolvedToday, expired = s.Expired, cancelled = s.Cancelled, frozen = s.Frozen, freezeReason = s.FreezeReason };
        });
        group.MapGet("/live", async (AdminOperations ops, [FromQuery] long? playerId = null) =>
            (await ops.ListLive(Actor.Api, playerId)).Select(s => new { id = s.Id, kind = ((LiveKind)s.Kind).ToString(), s.HostPlayerId, s.GuestPlayerId, state = s.State.ToString(), s.RaidId, s.SnapshotVersion, endReason = s.EndReason is { } er ? ((LiveEndReason)er).ToString() : null, s.CreatedAt, s.StartedAt, s.EndedAt }));
        group.MapPost("/live/{id:long}/end", async (long id, ReasonBody body, AdminOperations ops) =>
            await ops.EndLive(Actor.Api, id, body.Reason ?? "ended by admin") ? Results.Ok() : Results.NotFound());
        group.MapGet("/shop/{id:long}", async (long id, AdminOperations ops) =>
            (await ops.ListShop(Actor.Api, id)).Select(s => new { s.ThingId, s.DefName, s.StuffDef, s.Count, s.Quality, s.Price, s.SaveTick, s.Label, s.SoldAt }));
        group.MapGet("/shop/{id:long}/removals", async (long id, AdminOperations ops) =>
            (await ops.ListRemovals(Actor.Api, id)).Select(r => new { r.Id, r.ThingId, r.Count, r.CreatedAt }));
        group.MapPost("/players/{id:long}/peaceful", async (long id, PeacefulBody body, AdminOperations ops) => { await ops.SetPeaceful(Actor.Api, id, body.Peaceful); return Results.Ok(); });

        group.MapPut("/modpack", async (ModpackBody body, ModpackService modpack) =>
        {
            await modpack.Set(body.Entries.Select(e => new ModpackEntry { Position = e.Position, PackageId = e.PackageId, Version = e.Version ?? "", WorkshopId = e.WorkshopId, Url = e.Url ?? "", Kind = e.Kind }).ToList(), Role.Admin, "api");
            return Results.Ok();
        });
    }

    private static async ValueTask<object?> RequireToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        string? expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.ApiToken;
        string? header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(expected) || header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        byte[] given = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        byte[] want = Encoding.UTF8.GetBytes(expected);
        if (given.Length != want.Length || !CryptographicOperations.FixedTimeEquals(given, want)) return Results.Unauthorized();

        try
        {
            return await next(context);
        }
        catch (RpcException ex)
        {
            int status = ex.StatusCode switch
            {
                StatusCode.NotFound => StatusCodes.Status404NotFound,
                StatusCode.InvalidArgument or StatusCode.FailedPrecondition => StatusCodes.Status400BadRequest,
                StatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            };
            return Results.Problem(ex.Status.Detail, statusCode: status);
        }
    }
}
