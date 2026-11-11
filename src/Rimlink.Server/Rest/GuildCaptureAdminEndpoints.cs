using Microsoft.AspNetCore.Http.Features;
using global::Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;

namespace Rimlink.Server.Rest;

public static partial class OutpostAdminEndpoints
{
    private sealed record GuildPlacementDraft(int Tile, GuildResource Resource, string Reason);
    private sealed record GuildPlacementBody(GuildPlacementCommand Command, string IntentDigest, bool Confirmed);
    private sealed record GuildProfileRevokeBody(GuildMutationScope Scope, string RequestId, string ApprovalTransitionId, string Reason);
    private sealed record GuildCampaignCancelBody(GuildMutationScope Scope, string RequestId, string CampaignId, string ExpectedHeadId, string Reason);
    private static void MapGuildCaptureAdmin(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/guild-capture").RequireAdmin();
        group.AddEndpointFilter(async(context,next)=>
        {
            var http=context.HttpContext;
            await http.RequestServices.GetRequiredService<RateLimitInterceptor>().Acquire("rest:"+http.ReceiptIdentity(),"/rimlink.v1.GuildAdmin/Command",http.RequestAborted);
            try{return await next(context);}
            catch(BadHttpRequestException error){return Results.Problem(error.Message,statusCode:error.StatusCode);}
            catch(System.Text.Json.JsonException){return Results.BadRequest();}
            catch(InvalidDataException){return Results.BadRequest();}
            catch(OutpostEvidenceUnavailableException error){return Results.Json(new{code="RecoveryRequired",reason=error.Message,noReplayAuthorization=true,durableRejection=false},statusCode:409);}
            catch(RpcException error) when(error.StatusCode==StatusCode.FailedPrecondition)
            {return Results.Conflict(new{code="FailedPrecondition",reason=error.Status.Detail});}
        });
        group.MapGet("", async (HttpContext context, OutpostAdminService service, BattleExecutorRegistry executors,
            int? limit, CancellationToken ct) =>
        {
            if (limit is < 1 or > 100) return Results.BadRequest();
            return Results.Json(await service.InspectGuildCaptureAsync(context.Actor(), executors, limit ?? 32, ct));
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/cancel", async (HttpContext context, GuildOutpostService service, CancellationToken ct) =>
        {
            var request = await ReadCommandJson<GuildCampaignCancelBody>(context, ct);
            return Results.Json(await service.CancelCampaignAsync(context.Actor(), request.Scope, request.RequestId,
                request.CampaignId, request.ExpectedHeadId, request.Reason, ct));
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/freeze", async (HttpContext context, GuildOutpostService service, CancellationToken ct) =>
            Results.Json(await service.SetCaptureFrozenAsync(context.Actor(), await ReadCommandJson<GuildFreezeCommand>(context, ct), ct)))
            .RequireScope(ApiScope.Admin);
        group.MapPost("/abort", async (HttpContext context, GuildOutpostService service, CancellationToken ct) =>
            Results.Json(await service.AbortStartedBattleAsync(context.Actor(), await ReadCommandJson<GuildStartedAbortCommand>(context, ct), ct)))
            .RequireScope(ApiScope.Admin);
        group.MapPost("/place/preview", async (HttpContext context, GuildOutpostService service, OutpostMainObservation observation, CancellationToken ct) =>
        {
            var draft = await ReadCommandJson<GuildPlacementDraft>(context, ct);
            return Results.Json(await service.PreviewPlacementAsync(context.Actor(), observation, draft.Tile, draft.Resource, draft.Reason, ct));
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/place",async(HttpContext context,GuildOutpostService service,CancellationToken ct)=>
        {
            var request=await ReadCommandJson<GuildPlacementBody>(context,ct);
            return Results.Json(await service.CommitPlacementAsync(context.Actor(),request.Command,request.IntentDigest,request.Confirmed,ct));
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/profile/approve",async(HttpContext context,GuildOutpostService service,CancellationToken ct)=>
            Results.Json(await service.ApproveBattleProfileAsync(context.Actor(),await ReadCommandJson<GuildProfileApprovalRequest>(context,ct),ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/profile/revoke", async (HttpContext context, GuildOutpostService service, CancellationToken ct) =>
        {
            var request = await ReadCommandJson<GuildProfileRevokeBody>(context, ct);
            return Results.Json(await service.RevokeBattleProfileAsync(context.Actor(), request.Scope, request.RequestId,
                request.ApprovalTransitionId, request.Reason, ct));
        }).RequireScope(ApiScope.Admin);
        group.MapPost("/profile/stage/{kind}",async(BattleDocumentKind kind,HttpContext context,GuildOutpostService service,CancellationToken ct)=>
        {
            if(context.Request.ContentLength>BattleArtifactStore.MaximumBytes)throw new BadHttpRequestException("guild profile document too large",413);
            if(context.Features.Get<IHttpMaxRequestBodySizeFeature>() is{IsReadOnly:false} limit)limit.MaxRequestBodySize=BattleArtifactStore.MaximumBytes;
            return Results.Json(await service.StageProfileDocumentAsync(context.Actor(),kind,context.Request.Body,ct));
        }).RequireScope(ApiScope.Admin);
    }
}
