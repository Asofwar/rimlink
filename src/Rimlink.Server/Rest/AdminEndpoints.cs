using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Chat;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Saves;

namespace Rimlink.Server.Rest;

/// <summary>REST admin API under /api/admin, authorized by an API token or the panel session (<see cref="AdminAuth"/>). Used by the CLI, the web panel and the Discord bot.</summary>
public static class AdminEndpoints
{
    public sealed record BanBody(long? PlayerId, long? AccountId, long? SteamId, string? Ip, string Reason, double? Days);
    public sealed record KickBody(string? Reason);
    public sealed record MuteBody(double Minutes);
    public sealed record TextBody(string Text);
    public sealed record ChatBody(string Author, string Text);
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
        var group = app.MapGroup("/api/admin").RequireAdmin();

        group.MapGet("/players/online", (HttpContext ctx, AdminOperations ops) => ops.ListOnline(ctx.Actor())).RequireScope(ApiScope.Moderate);
        group.MapPost("/players/{id:long}/kick", (long id, KickBody body, HttpContext ctx, AdminOperations ops) => ops.Kick(ctx.Actor(), id, body.Reason ?? "kicked by an admin")).RequireScope(ApiScope.Moderate);
        group.MapPost("/players/{id:long}/mute", (long id, MuteBody body, HttpContext ctx, AdminOperations ops) => ops.Mute(ctx.Actor(), id, TimeSpan.FromMinutes(body.Minutes))).RequireScope(ApiScope.Moderate);
        group.MapDelete("/players/{id:long}/mute", (long id, HttpContext ctx, AdminOperations ops) => ops.Unmute(ctx.Actor(), id)).RequireScope(ApiScope.Moderate);
        group.MapGet("/players/{id:long}/saves", async (long id, HttpContext ctx, AdminOperations ops) => (await ops.ListSaves(ctx.Actor(), id)).Select(SaveStore.ToInfo).Select(s => new { s.Version, s.Size, s.GameTick, s.IsCurrent, CreatedAt = s.CreatedAt.ToDateTime() })).RequireScope(ApiScope.Admin);
        group.MapPost("/players/{id:long}/saves/{version:int}/rollback", async (long id, int version, HttpContext ctx, AdminOperations ops) =>
            await ops.RollbackSave(ctx.Actor(), id, version) ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);
        group.MapPost("/players/{id:long}/reset", (long id, HttpContext ctx, AdminOperations ops) => ops.ResetPlayer(ctx.Actor(), id)).RequireScope(ApiScope.Admin);

        group.MapGet("/bans", async (HttpContext ctx, AdminOperations ops, AdminDirectory directory) =>
        {
            var bans = await ops.ListBans(ctx.Actor());
            var names = await directory.NamesByAccount(bans.Where(b => b.AccountId is not null).Select(b => b.AccountId!.Value));
            return bans.Select(b => new { b.Id, b.AccountId, b.SteamId, steamIdText = b.SteamId?.ToString(), b.Ip, b.Reason, b.ExpiresAt, b.CreatedAt, playerName = b.AccountId is { } a && names.TryGetValue(a, out var n) ? n : null });
        }).RequireScope(ApiScope.Moderate);
        group.MapPost("/bans", async (BanBody body, HttpContext ctx, AdminOperations ops) =>
        {
            long? playerId = body.PlayerId;
            long? accountId = body.AccountId;
            if (playerId is null && accountId is { } acc) playerId = (await ops.FindPlayerByAccount(acc)).Id;
            var ban = await ops.Ban(ctx.Actor(), playerId, body.SteamId, body.Ip, body.Reason, body.Days is { } d ? TimeSpan.FromDays(d) : null);
            return Results.Ok(new { ban.Id });
        }).RequireScope(ApiScope.Admin);
        group.MapDelete("/bans/{id:long}", async (long id, HttpContext ctx, AdminOperations ops) => await ops.Pardon(ctx.Actor(), id) ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);

        group.MapPost("/chat", async (ChatBody body, ChatService chat) => Results.Ok(new { id = await chat.External(body.Author ?? "", body.Text ?? "") })).RequireScope(ApiScope.Chat);
        group.MapPost("/broadcast", (TextBody body, HttpContext ctx, AdminOperations ops) => ops.Broadcast(ctx.Actor(), body.Text)).RequireScope(ApiScope.Moderate);
        group.MapPost("/shutdown", (ShutdownBody body, HttpContext ctx, AdminOperations ops) => ops.Shutdown(ctx.Actor(), body.InSeconds, body.Reason ?? "server shutdown")).RequireScope(ApiScope.Admin);
        group.MapPost("/world/recreate", (HttpContext ctx, AdminOperations ops) => ops.RecreateWorld(ctx.Actor())).RequireScope(ApiScope.Admin);
        group.MapGet("/actions", async (HttpContext ctx, AdminOperations ops, AdminDirectory directory, [FromQuery] int limit = 100, [FromQuery] long? before = null) =>
        {
            var actions = await ops.ListActions(ctx.Actor(), limit, before);
            var names = await directory.NamesByAccount(actions.Where(a => a.TargetAccountId is not null).Select(a => a.TargetAccountId!.Value));
            return actions.Select(a => new { a.Id, a.At, a.ActorAccountId, a.ActorName, a.Action, a.TargetAccountId, targetName = a.TargetAccountId is { } t && names.TryGetValue(t, out var n) ? n : null, a.Details });
        }).RequireScope(ApiScope.Moderate);

        group.MapGet("/modpack", async (ModpackService modpack) => new ModpackBody((await modpack.Get()).Select(e => new ModpackEntryBody(e.Position, e.PackageId, e.Version, e.WorkshopId, e.Url, e.Kind)).ToList())).RequireScope(ApiScope.Read);
        group.MapPut("/modpack/defs", async (DefsBody body, HttpContext ctx, ModpackService modpack, ModpackDefs defs) =>
        {
            await modpack.SetDefs(body.DefNames, ctx.Actor().Role, ctx.Actor().Name, defs);
            return Results.Ok(new { count = defs.Count });
        }).RequireScope(ApiScope.Admin);

        group.MapPost("/economy/wallet/{id:long}", async (long id, WalletBody body, HttpContext ctx, AdminOperations ops) =>
            Results.Ok(new { balance = await ops.AdjustWallet(ctx.Actor(), id, body.Delta, body.Reason ?? "admin adjustment") })).RequireScope(ApiScope.Admin);
        group.MapGet("/economy/parcels/{id:long}", async (long id, HttpContext ctx, AdminOperations ops, AdminDirectory directory) =>
        {
            var parcels = await ops.ListParcels(ctx.Actor(), id);
            var names = await directory.NamesByPlayer(parcels.Select(p => p.ToPlayerId).Concat(parcels.Where(p => p.FromPlayerId is not null).Select(p => p.FromPlayerId!.Value)));
            return parcels.Select(p => new { p.Id, Kind = p.Kind.ToString(), p.FromPlayerId, fromName = NameOf(names, p.FromPlayerId), p.ToPlayerId, toName = NameOf(names, p.ToPlayerId), p.Silver, p.SizeBytes, p.CreatedAt, p.DeliveredAt, p.DeliveredTick });
        }).RequireScope(ApiScope.Moderate);
        group.MapGet("/economy/lots", async (HttpContext ctx, AdminOperations ops, [FromQuery] long? playerId = null) =>
            await ops.ListLotViews(ctx.Actor(), playerId)).RequireScope(ApiScope.Read);
        group.MapPost("/economy/refund/lot/{id:long}", async (long id, HttpContext ctx, AdminOperations ops) => await ops.RefundLot(ctx.Actor(), id) ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);
        group.MapPost("/economy/refund/parcel/{id:long}", async (long id, HttpContext ctx, AdminOperations ops) => await ops.RefundParcel(ctx.Actor(), id) ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);
        group.MapPost("/economy/freeze", async (FreezeBody body, HttpContext ctx, AdminOperations ops) => { await ops.SetEconomyFrozen(ctx.Actor(), body.Frozen, body.Reason); return Results.Ok(); }).RequireScope(ApiScope.Admin);
        group.MapGet("/economy/stats", async (HttpContext ctx, AdminOperations ops) =>
        {
            var s = await ops.EconomyStats(ctx.Actor());
            return new { silverInWallets = s.SilverInWallets, openLots = s.OpenLots, pendingParcels = s.PendingParcels, tradesToday = s.TradesToday, frozen = s.Frozen, freezeReason = s.FreezeReason };
        }).RequireScope(ApiScope.Read);

        group.MapGet("/raids", async (HttpContext ctx, AdminOperations ops, AdminDirectory directory, [FromQuery] long? playerId = null) =>
        {
            var raids = await ops.ListRaids(ctx.Actor(), playerId);
            var names = await directory.NamesByPlayer(raids.SelectMany(r => new[] { r.AttackerPlayerId, r.TargetPlayerId }));
            return raids.Select(r => new { r.Id, r.AttackerPlayerId, attackerName = NameOf(names, r.AttackerPlayerId), r.TargetPlayerId, targetName = NameOf(names, r.TargetPlayerId), State = r.State.ToString(), PawnMode = ((PawnMode)r.PawnMode).ToString(), Timing = ((RaidTiming)r.Timing).ToString(), r.LootCap, r.CreatedAt, r.LandAtTick, r.LandedTick, r.ResolvedAt, r.ExpiresAt });
        }).RequireScope(ApiScope.Read);
        group.MapGet("/trades", async (HttpContext ctx, AdminOperations ops, [FromQuery] long? playerId = null) => await ops.ListTrades(ctx.Actor(), playerId)).RequireScope(ApiScope.Moderate);
        group.MapPost("/trades/{id:long}/cancel", async (long id, ReasonBody body, HttpContext ctx, AdminOperations ops) =>
            await ops.CancelTrade(ctx.Actor(), id, body.Reason ?? "cancelled by admin") ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);
        group.MapPost("/raids/{id:long}/cancel", async (long id, CancelRaidBody body, HttpContext ctx, AdminOperations ops) =>
            await ops.CancelRaid(ctx.Actor(), id, body.Reason ?? "cancelled by admin", body.ReturnPawns) ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);
        group.MapPost("/raids/freeze", async (FreezeBody body, HttpContext ctx, AdminOperations ops) => { await ops.SetRaidsFrozen(ctx.Actor(), body.Frozen, body.Reason); return Results.Ok(); }).RequireScope(ApiScope.Admin);
        group.MapGet("/raids/stats", async (HttpContext ctx, AdminOperations ops) =>
        {
            var s = await ops.RaidStats(ctx.Actor());
            return new { queued = s.Queued, announced = s.Announced, landed = s.Landed, resolvedToday = s.ResolvedToday, expired = s.Expired, cancelled = s.Cancelled, frozen = s.Frozen, freezeReason = s.FreezeReason };
        }).RequireScope(ApiScope.Read);
        group.MapGet("/live", async (HttpContext ctx, AdminOperations ops, AdminDirectory directory, [FromQuery] long? playerId = null) =>
        {
            var sessions = await ops.ListLive(ctx.Actor(), playerId);
            var names = await directory.NamesByPlayer(sessions.SelectMany(s => new[] { s.HostPlayerId, s.GuestPlayerId }));
            return sessions.Select(s => new { id = s.Id, kind = ((LiveKind)s.Kind).ToString(), s.HostPlayerId, hostName = NameOf(names, s.HostPlayerId), s.GuestPlayerId, guestName = NameOf(names, s.GuestPlayerId), state = s.State.ToString(), s.RaidId, s.SnapshotVersion, endReason = s.EndReason is { } er ? ((LiveEndReason)er).ToString() : null, s.CreatedAt, s.StartedAt, s.EndedAt });
        }).RequireScope(ApiScope.Read);
        group.MapPost("/live/{id:long}/end", async (long id, ReasonBody body, HttpContext ctx, AdminOperations ops) =>
            await ops.EndLive(ctx.Actor(), id, body.Reason ?? "ended by admin") ? Results.Ok() : Results.NotFound()).RequireScope(ApiScope.Admin);
        group.MapGet("/shop/{id:long}", async (long id, HttpContext ctx, AdminOperations ops) =>
            (await ops.ListShop(ctx.Actor(), id)).Select(s => new { s.ThingId, s.DefName, s.StuffDef, s.Count, s.Quality, s.Price, s.SaveTick, s.Label, s.SoldAt })).RequireScope(ApiScope.Moderate);
        group.MapGet("/shop/{id:long}/removals", async (long id, HttpContext ctx, AdminOperations ops) =>
            (await ops.ListRemovals(ctx.Actor(), id)).Select(r => new { r.Id, r.ThingId, r.Count, r.CreatedAt })).RequireScope(ApiScope.Moderate);
        group.MapPost("/players/{id:long}/peaceful", async (long id, PeacefulBody body, HttpContext ctx, AdminOperations ops) => { await ops.SetPeaceful(ctx.Actor(), id, body.Peaceful); return Results.Ok(); }).RequireScope(ApiScope.Admin);

        group.MapPut("/modpack", async (ModpackBody body, HttpContext ctx, ModpackService modpack) =>
        {
            await modpack.Set(body.Entries.Select(e => new ModpackEntry { Position = e.Position, PackageId = e.PackageId, Version = e.Version ?? "", WorkshopId = e.WorkshopId, Url = e.Url ?? "", Kind = e.Kind }).ToList(), ctx.Actor().Role, ctx.Actor().Name);
            return Results.Ok();
        }).RequireScope(ApiScope.Admin);
    }

    private static string? NameOf(Dictionary<long, string> names, long? playerId) =>
        playerId is { } id && names.TryGetValue(id, out var name) ? name : null;
}
