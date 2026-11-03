using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Rimlink.Protocol.ModpackDelivery;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Rest;

public static class ModpackDeliveryEndpoints
{
    public static void MapModpackDelivery(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/modpack/delivery").RequireAdmin();
        admin.AddEndpointFilter(Errors);
        admin.MapGet("/requirements", async (ModpackDeliveryService service, CancellationToken ct) => Json(await service.Requirements(ct))).RequireScope(ApiScope.Read);
        admin.MapGet("/publication", async (ModpackDeliveryService service, CancellationToken ct) => Json(await service.Publication(ct))).RequireScope(ApiScope.Read);
        admin.MapPost("/packages", async (HttpContext ctx, ModpackDeliveryService service) => Json(await service.Offer(await Body<UploadOffer>(ctx, DeliveryLimits.MaxManifestBytes + 16 * 1024), ctx.Actor(), ctx.RequestAborted))).RequireScope(ApiScope.Admin);
        admin.MapPut("/packages/{id}/archive", async (string id, HttpContext ctx, ModpackDeliveryService service) =>
        {
            LimitBody(ctx, DeliveryLimits.MaxArchiveBytes);
            await service.Upload(id, ctx.Request.Body, ctx.Request.ContentLength, ctx.Request.ContentType, ctx.RequestAborted);
            return Json(new { verified = true });
        }).RequireScope(ApiScope.Admin);
        admin.MapPost("/packages/{id}/publish", async (string id, HttpContext ctx, ModpackDeliveryService service) => Json(await service.Publish(id, await Body<PublishPackage>(ctx, DeliveryLimits.SimpleBodyBytes), ctx.Actor(), ctx.RequestAborted))).RequireScope(ApiScope.Admin);
        admin.MapDelete("/publication", async (HttpContext ctx, ModpackDeliveryService service) => Json(await service.Unpublish(ctx.Actor(), ctx.RequestAborted))).RequireScope(ApiScope.Admin);

        var bootstrap = app.MapGroup("/api/modpack-delivery"); bootstrap.AddEndpointFilter(Errors);
        bootstrap.MapGet("/capabilities", async (HttpContext ctx, ModpackDeliveryService service, DeliveryRuntime runtime, IOptions<ServerOptions> server, IOptions<AuthOptions> auth, ISteamTicketVerifier steam) =>
        {
            Attempt(ctx, runtime);
            return Json(new DeliveryCapabilities(DeliveryLimits.FormatVersion, (await service.Publication(ctx.RequestAborted)).Available, !string.IsNullOrEmpty(server.Value.ServerPassword), auth.Value.AllowPassword, steam.Configured));
        });
        bootstrap.MapPost("/access", async (HttpContext ctx, ModpackDeliveryService service, DeliveryRuntime runtime) =>
        {
            Attempt(ctx, runtime);
            return Json(await service.Access(await Body<AccessRequest>(ctx, DeliveryLimits.SimpleBodyBytes), ctx.RequestAborted));
        });
        bootstrap.MapGet("/packages/{id}/manifest", async (string id, HttpContext ctx, ModpackDeliveryService service) => Json((await service.Authorize(Token(ctx), id, ctx.RequestAborted)).Manifest));
        bootstrap.MapGet("/packages/{id}/settings", async (string id, HttpContext ctx, ModpackDeliveryService service) => Json((await service.Authorize(Token(ctx), id, ctx.RequestAborted)).Settings));
        bootstrap.MapGet("/packages/{id}/archive", StreamArchive);
    }
    private static IResult Json<T>(T body) => Results.Json(body, DeliveryJson.Options);
    private static void Attempt(HttpContext ctx, DeliveryRuntime runtime)
    {
        var ip = ctx.Connection.RemoteIpAddress; if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        runtime.Attempt(ip?.ToString() ?? "unknown");
    }
    private static string Token(HttpContext ctx)
    {
        string value = ctx.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.Ordinal) && value.Length <= 71 ? value[7..] : "";
    }
    private static void LimitBody(HttpContext ctx, long limit)
    {
        if (ctx.Request.ContentLength > limit) throw new DeliveryException("Limit");
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit;
    }
    private static async Task<T> Body<T>(HttpContext ctx, int limit)
    {
        LimitBody(ctx, limit);
        if (ctx.Request.ContentType?.Split(';')[0].Trim() != "application/json") throw new DeliveryException("InvalidManifest");
        using var bytes = new MemoryStream(); var buffer = new byte[Math.Min(limit + 1, 8192)];
        while (true)
        {
            int read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted); if (read == 0) break;
            if (bytes.Length + read > limit) throw new DeliveryException("Limit");
            bytes.Write(buffer, 0, read);
        }
        return DeliveryJson.Read<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), limit);
    }
    private static async ValueTask<object?> Errors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ctx = context.HttpContext; ctx.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (DeliveryException ex)
        {
            int status = ex.Code switch { "Unauthorized" => 401, "NotFound" => 404, "StaleCatalog" => 409, "Busy" or "RateLimit" => 429, "Unavailable" => 503, "Limit" => 413, "DiskSpace" => 507, _ => 400 };
            if (status == 429) ctx.Response.Headers.RetryAfter = "60";
            return Results.Json(ex.Error, DeliveryJson.Options, statusCode: status);
        }
        catch (IOException) { return Results.Json(new DeliveryException("Storage", retryable: true).Error, DeliveryJson.Options, statusCode: 503); }
    }
    private static async Task StreamArchive(string id, HttpContext ctx, ModpackDeliveryService service, DeliveryRuntime runtime)
    {
        var (manifest, _, grant) = await service.Authorize(Token(ctx), id, ctx.RequestAborted);
        using var lease = runtime.Transfer(grant);
        string etag = "\"" + manifest.ArchiveSha256 + "\"";
        long start = 0, end = manifest.ArchiveBytes - 1;
        string rangeText = ctx.Request.Headers.Range.ToString(), ifRange = ctx.Request.Headers.IfRange.ToString();
        bool ranged = rangeText.Length > 0 && (ifRange.Length == 0 || ifRange == etag);
        if (ranged)
        {
            if (!RangeHeaderValue.TryParse(rangeText, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1)
            { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = "bytes */" + manifest.ArchiveBytes; return; }
            var part = range.Ranges.Single();
            if (part.From is { } from) { start = from; end = Math.Min(part.To ?? end, end); }
            else if (part.To is { } suffix && suffix > 0) start = Math.Max(0, manifest.ArchiveBytes - suffix);
            else start = manifest.ArchiveBytes;
            if (start >= manifest.ArchiveBytes || end < start)
            { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = "bytes */" + manifest.ArchiveBytes; return; }
        }
        await using var file = new FileStream(service.ArchivePath(id), FileMode.Open, FileAccess.Read, FileShare.Read, DeliveryLimits.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Position = start;
        ctx.Response.StatusCode = ranged ? 206 : 200; ctx.Response.ContentType = "application/zip"; ctx.Response.ContentLength = end - start + 1;
        ctx.Response.Headers.ETag = etag; ctx.Response.Headers.AcceptRanges = "bytes";
        if (ranged) ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{manifest.ArchiveBytes}";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, grant.Cancellation.Token);
        cancel.CancelAfter(runtime.TimeLeft(grant));
        var buffer = new byte[DeliveryLimits.BufferBytes]; long remaining = end - start + 1;
        try
        {
            while (remaining > 0)
            {
                runtime.Check(grant);
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancel.Token);
                if (read == 0) throw new IOException("short package");
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancel.Token); remaining -= read;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or DeliveryException or IOException) { ctx.Abort(); }
    }
}
