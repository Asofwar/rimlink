using Microsoft.Extensions.Options;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Rest;

/// <summary>Serves wwwroot: the public page at / and the admin panel at /admin/, gated by <see cref="PanelOptions"/> and with strict headers.</summary>
public static class PanelHosting
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public static void UsePanelStaticFiles(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            // gRPC is POST and the APIs have their own gates; only page and asset reads are decided here.
            if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || request.Path.StartsWithSegments("/api")
                || request.Path.StartsWithSegments("/health") || request.Path.StartsWithSegments("/metrics"))
            {
                await next(context);
                return;
            }

            var panel = context.RequestServices.GetRequiredService<IOptions<PanelOptions>>().Value;
            bool hasToken = !string.IsNullOrWhiteSpace(context.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.ApiToken);
            string path = request.Path.Value ?? "/";
            bool allowed = path.StartsWith("/admin", StringComparison.Ordinal) ? panel.Enabled && hasToken
                : path is "/" or "/index.html" or "/public.js" ? panel.Enabled && panel.Public
                : panel.Enabled;
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (path == "/admin")
            {
                context.Response.Redirect("/admin/");
                return;
            }
            await next(context);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = file =>
            {
                var headers = file.Context.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                // Revalidate every load so an update never runs against stale scripts.
                headers.CacheControl = "no-cache";
            },
        });
    }
}
