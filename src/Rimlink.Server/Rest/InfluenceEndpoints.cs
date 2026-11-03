using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Influence;
using Rimlink.Server.Grpc;

namespace Rimlink.Server.Rest;

public static class InfluenceEndpoints
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ContextBody(string WorldInstanceId, long SettlementId, long SaveGeneration, long AllianceId)
    {
        public InfluenceContext Proto() => new() { WorldInstanceId = WorldInstanceId ?? "", SettlementId = SettlementId, SaveGeneration = SaveGeneration, AllianceId = AllianceId };
        public static ContextBody From(InfluenceContext c) => new(c.WorldInstanceId, c.SettlementId, c.SaveGeneration, c.AllianceId);
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record DisableBody(string RequestId, long ExpectedVersion, ContextBody Context);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ColorBody(string WorldId, int PaletteIndex, string RequestId);
    public sealed record SettingBody(ContextBody Context, long Version, bool Enabled, bool? RootSurface, int Tile, long ArchiveId);
    public sealed record AllianceBody(long Id, string Name, int PaletteIndex);
    public sealed record SourceBody(long Id, long AllianceId, int Tile, int Radius);
    public sealed record SnapshotBody(string WorldInstanceId, string ProcessInstanceId, long Revision, int AlgorithmVersion, AllianceBody[] Alliances, SourceBody[] Sources);

    public static void MapInfluenceApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/influence").RequireAdmin();
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            string method = HttpMethods.IsGet(http.Request.Method) ? http.Request.Path.Value!.EndsWith("influence", StringComparison.Ordinal) ? "Get" : "GetSetting" : "Mutate";
            await http.RequestServices.GetRequiredService<RateLimitInterceptor>().Acquire("rest:" + http.ReceiptIdentity(), "/rimlink.v1.Influence/" + method, http.RequestAborted);
            return await next(ctx);
        });
        group.MapGet("", async (InfluenceService service, CancellationToken ct) =>
        {
            try
            {
                var p = await service.GetSnapshot(ct);
                var body = new SnapshotBody(p.WorldInstanceId, p.ProcessInstanceId, p.Revision, p.AlgorithmVersion,
                    p.Alliances.Select(a => new AllianceBody(a.Id, a.Name, a.PaletteIndex)).ToArray(), p.Sources.Select(s => new SourceBody(s.Id, s.AllianceId, s.Tile, s.Radius)).ToArray());
                if (JsonSerializer.SerializeToUtf8Bytes(body).Length > InfluenceLimits.MaxJsonBytes) return Results.Problem("complete influence snapshot is too large", statusCode: 503);
                return Results.Ok(body);
            }
            catch (RpcException ex) when (ex.Trailers.Any(t => t.Key == RpcErrors.DetailTrailer && ErrorDetail.Parser.ParseFrom(t.ValueBytes).Code == ErrorCode.InfluenceUnavailable))
            { return Results.Problem("complete influence snapshot is unavailable", statusCode: 503); }
        }).RequireScope(ApiScope.Read);
        group.MapGet("/settings/{id:long}", async (long id, HttpContext ctx, InfluenceAdminService service, CancellationToken ct) =>
            View(await service.Setting(ctx.Actor(), new InfluenceTarget { SettlementId = id }, ct))).RequireScope(ApiScope.Admin);
        group.MapGet("/archive/{id:long}/setting", async (long id, HttpContext ctx, InfluenceAdminService service, CancellationToken ct) =>
            View(await service.Setting(ctx.Actor(), new InfluenceTarget { ArchiveId = id }, ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/settings/{id:long}/disable", async (long id, DisableBody body, HttpContext ctx, InfluenceAdminService service, CancellationToken ct) =>
            body.Context is null ? Results.BadRequest() : Result(await service.Disable(ctx.Actor(), ctx.ReceiptIdentity(), new InfluenceTarget { SettlementId = id }, body.RequestId ?? "", body.Context.Proto(), body.ExpectedVersion, ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/archive/{id:long}/disable", async (long id, DisableBody body, HttpContext ctx, InfluenceAdminService service, CancellationToken ct) =>
            body.Context is null ? Results.BadRequest() : Result(await service.Disable(ctx.Actor(), ctx.ReceiptIdentity(), new InfluenceTarget { ArchiveId = id }, body.RequestId ?? "", body.Context.Proto(), body.ExpectedVersion, ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/alliances/{id:long}/color", async (long id, ColorBody body, HttpContext ctx, InfluenceAdminService service, CancellationToken ct) =>
            Result(await service.Color(ctx.Actor(), ctx.ReceiptIdentity(), id, body.WorldId ?? "", body.PaletteIndex, body.RequestId ?? "", ct))).RequireScope(ApiScope.Admin);
    }

    private static SettingBody View(InfluenceSetting p) => new(ContextBody.From(p.Context), p.Version, p.Enabled, p.HasRootSurface ? p.RootSurface : null, p.Tile, p.ArchiveId);
    private static IResult Result(InfluenceReceipt receipt) => receipt.Error is not { } error
        ? Results.Ok(new { receipt.AppliedVersion, receipt.PublicRevision })
        : Results.Problem(error.Message, statusCode: error.Code switch { ErrorCode.InfluenceConflict or ErrorCode.InfluenceCapacity or ErrorCode.InfluenceIntent or ErrorCode.InfluenceIneligible => 409, ErrorCode.InfluenceUnavailable => 503, _ => 400 });
}
