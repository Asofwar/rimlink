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
