using Microsoft.Extensions.Options;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Hosting;

namespace Rimlink.Server.Rest;

/// <summary>Web panel login: <c>Admin:ApiToken</c> in, an HttpOnly SameSite=Strict session cookie out (admin panel spec §3).</summary>
public static class PanelEndpoints
{
    public const string CookieName = "rimlink_panel";
    public const string PanelHeader = "X-Rimlink-Panel";

    public sealed record LoginBody(string? Token);

    public static void MapPanelApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/panel").AddEndpointFilter(Gate);

        group.MapPost("/login", (LoginBody body, HttpContext ctx, PanelSessions sessions, IOptions<AdminOptions> admin, IOptions<PanelOptions> panel, ILogger<PanelSessions> log) =>
        {
            string ip = ClientAddress.Normalize(ctx.Connection.RemoteIpAddress);
            if (sessions.IsLockedOut(ip, out var retryAfter))
            {
                ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Problem("too many failed logins; try again later", statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (!AdminToken.Matches(admin.Value.ApiToken, body.Token))
            {
                sessions.RecordFailure(ip);
                log.LogWarning("Failed panel login from {Ip}", ip);
                return Results.Unauthorized();
            }

            var session = sessions.Create();
            ctx.Response.Cookies.Append(CookieName, session.Id, CookieOptions(panel.Value.SessionLength));
            return Results.Ok(new { expiresAt = session.ExpiresAt });
        });

        group.MapPost("/logout", (HttpContext ctx, PanelSessions sessions) =>
        {
            if (ctx.Request.Cookies[CookieName] is { } id) sessions.End(id);
            ctx.Response.Cookies.Delete(CookieName, CookieOptions(null));
            return Results.Ok();
        });

        group.MapGet("/session", (HttpContext ctx, PanelSessions sessions) =>
            sessions.Find(ctx.Request.Cookies[CookieName]) is { } session ? Results.Ok(new { expiresAt = session.ExpiresAt }) : Results.Unauthorized());
    }

    private static CookieOptions CookieOptions(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        MaxAge = maxAge,
    };

    /// <summary>404 while the panel is off or <c>Admin:ApiToken</c> is blank; CSRF check on POST; never cached.</summary>
    private static async ValueTask<object?> Gate(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        bool enabled = http.RequestServices.GetRequiredService<IOptions<PanelOptions>>().Value.Enabled
            && !string.IsNullOrWhiteSpace(http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.ApiToken);
        if (!enabled) return Results.NotFound();

        http.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsGet(http.Request.Method) && !AdminAuth.PassesCsrf(http.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(context);
    }
}
