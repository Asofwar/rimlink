using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.Http.Features;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Grpc;

namespace Rimlink.Server.Rest;

public static class OutpostAdminEndpoints
{
    private const int MaxJsonBytes = 360 * 1024; // One base64-encoded 256 KiB chunk plus bounded metadata.
    public static void MapOutpostAdmin(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/outposts/manifest").RequireAdmin();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            string method = http.Request.Path.Value!.Split('/')[^1] switch
            { "binding" => "GetOutpostManifestBinding", "stage" => "StageOutpostManifest", "read" => "ReadOutpostManifest", _ => "ConfirmOutpostManifest" };
            await http.RequestServices.GetRequiredService<RateLimitInterceptor>().Acquire("rest:" + http.ReceiptIdentity(), "/rimlink.v1.World/" + method, http.RequestAborted);
            try { return await next(context); }
            catch (BadHttpRequestException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            catch (InvalidJsonException) { return Results.BadRequest(); }
            catch (InvalidProtocolBufferException) { return Results.BadRequest(); }
        });
        group.MapGet("/binding", async (HttpContext context, OutpostManifestService service, CancellationToken ct) =>
            Json(await service.BindingAsync(context.Actor(), ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/stage", async (HttpContext context, OutpostManifestUploads uploads, OutpostManifestService service, CancellationToken ct) =>
            Json(await uploads.StageAsync(context.Actor(), "rest:" + context.ReceiptIdentity(), await Read<OutpostManifestChunk>(context, ct), service, ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/read", async (HttpContext context, OutpostManifestService service, CancellationToken ct) =>
            Json(await service.ReadAsync(context.Actor(), await Read<OutpostManifestPageRequest>(context, ct), ct))).RequireScope(ApiScope.Admin);
        group.MapPost("/confirm", async (HttpContext context, OutpostManifestService service, CancellationToken ct) =>
        {
            var request = await Read<OutpostManifestConfirmation>(context, ct);
            return Json(await service.ConfirmAsync(context.Actor(), request.ManifestId, request.ExpectedDigest, ct));
        }).RequireScope(ApiScope.Admin);
    }

    private static IResult Json(IMessage message) => Results.Text(JsonFormatter.Default.Format(message), "application/json", Encoding.UTF8);
    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct) where T : IMessage<T>, new()
    {
        var request = context.Request;
        if (request.ContentLength > MaxJsonBytes) throw TooLarge();
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = MaxJsonBytes;
        // Also enforce while streaming, including TestServer and requests without Content-Length.
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + count > MaxJsonBytes) throw TooLarge();
            body.Write(buffer, 0, count);
        }
        return JsonParser.Default.Parse<T>(Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
    }
    private static BadHttpRequestException TooLarge() => new("outpost manifest JSON body is too large", StatusCodes.Status413PayloadTooLarge);
}
