using Rimlink.Server.Grpc;
using Rimlink.Server.Rest;

namespace Rimlink.Server.Application.Influence;

public static class InfluenceFeature
{
    public static IServiceCollection AddInfluence(this IServiceCollection services) => services.AddSingleton<InfluenceProcessEpoch>().AddScoped<InfluenceService>().AddScoped<InfluenceAdminService>();
    public static void MapInfluence(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/api/admin/influence"))
            {
                if (ctx.Request.ContentLength > 4096) { ctx.Response.StatusCode = 413; return; }
                // Limit chunked requests too, before JSON model binding can buffer an unlimited body.
                byte[] body = new byte[4097];
                int count = 0, read;
                while (count < body.Length && (read = await ctx.Request.Body.ReadAsync(body.AsMemory(count), ctx.RequestAborted)) > 0) count += read;
                if (count > 4096) { ctx.Response.StatusCode = 413; return; }
                var original = ctx.Request.Body;
                using var bounded = new MemoryStream(body, 0, count, writable: false);
                ctx.Request.Body = bounded;
                try { await next(); } finally { ctx.Request.Body = original; }
            }
            else await next();
        });
        app.MapGrpcService<InfluenceGrpcService>();
        app.MapInfluenceApi();
    }
}
