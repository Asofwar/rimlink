using Microsoft.AspNetCore.Mvc;
using Rimlink.Server.Application.Admin;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Rest;

/// <summary>Admin routes added for the web panel and integrations (admin panel spec §4): directory, card, chat, status, backups.</summary>
public static class AdminDirectoryEndpoints
{
    public sealed record RoleBody(string Role);
    public sealed record RenameBody(string Name, string? FactionName);

    public static void MapAdminDirectoryApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAdmin();

        group.MapGet("/players", async (HttpContext ctx, AdminDirectory directory, [FromQuery] string? q = null, [FromQuery] int limit = 50, [FromQuery] int offset = 0) =>
        {
            bool full = ApiScopes.Grants(ctx.Scopes(), ApiScope.Moderate);
            var page = await directory.Search(q, limit, offset, full);
            return full
                ? Results.Ok(page)
                : Results.Ok(new { total = page.Total, items = page.Items.Select(r => new { r.Id, r.Name, r.FactionName, r.Online, r.Role }) });
        }).RequireScope(ApiScope.Read);

        group.MapGet("/players/{id:long}", async (long id, AdminDirectory directory) =>
        {
            var c = await directory.Card(id);
            var r = c.Player;
            return new
            {
                r.Id, r.AccountId, r.Name, r.FactionName, r.Role, r.SteamId, r.Login, r.Online, r.LastSeenAt, r.CreatedAt, r.MutedUntil, r.Peaceful, r.Banned,
                c.LastIp, c.Session, c.Settlements, c.WalletBalance, c.WealthTotal, c.FirstSaveAt, c.CurrentSave, c.ActiveBans, c.VisitsClosed, c.PeacefulOffAt,
            };
        }).RequireScope(ApiScope.Moderate);

        group.MapPost("/players/{id:long}/role", async (long id, RoleBody body, HttpContext ctx, AdminOperations ops) =>
        {
            if (!Enum.TryParse<Role>(body.Role, ignoreCase: true, out var role) || !string.Equals(role.ToString(), body.Role, StringComparison.OrdinalIgnoreCase))
                return Results.Problem("role must be player, moderator or admin", statusCode: StatusCodes.Status400BadRequest);
            await ops.SetRole(ctx.Actor(), id, role);
            return Results.Ok();
        }).RequireScope(ApiScope.Admin);

        group.MapPost("/players/{id:long}/rename", async (long id, RenameBody body, HttpContext ctx, AdminOperations ops) =>
        {
            await ops.RenamePlayer(ctx.Actor(), id, body.Name, body.FactionName);
            return Results.Ok();
        }).RequireScope(ApiScope.Admin);

        group.MapGet("/economy/wallet/{id:long}", async (long id, HttpContext ctx, AdminOperations ops) =>
            new { balance = await ops.WalletBalance(ctx.Actor(), id) }).RequireScope(ApiScope.Moderate);

        group.MapPost("/saveall", async (HttpContext ctx, AdminOperations ops) =>
        {
            await ops.SaveAll(ctx.Actor());
            return Results.Ok();
        }).RequireScope(ApiScope.Moderate);

        group.MapGet("/status", (ServerStatus status) => status.Admin()).RequireScope(ApiScope.Read);
        group.MapGet("/config", (ServerStatus status) => status.Config()).RequireScope(ApiScope.Admin);

        group.MapGet("/backups", (Backup.BackupService backups) => backups.List()).RequireScope(ApiScope.Admin);
        group.MapPost("/backups", async (HttpContext ctx, AdminOperations ops) =>
        {
            var file = await ops.CreateBackup(ctx.Actor());
            return new { file.Name, file.Size };
        }).RequireScope(ApiScope.Admin);
        group.MapGet("/backups/{name}", async (string name, HttpContext ctx, AdminOperations ops) =>
            await ops.OpenBackup(ctx.Actor(), name) is { } file ? Results.File(file.FullName, "application/octet-stream", name) : Results.NotFound()).RequireScope(ApiScope.Admin);

        group.MapGet("/chat", (AdminDirectory directory, [FromQuery] long? playerId = null, [FromQuery] long? before = null, [FromQuery] long? after = null, [FromQuery] int limit = 100) =>
            directory.Chat(playerId, before, after, limit)).RequireScope(ApiScope.Read);
    }
}
