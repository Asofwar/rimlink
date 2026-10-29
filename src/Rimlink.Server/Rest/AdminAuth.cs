using Grpc.Core;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Rest;

/// <summary>Endpoint metadata: the API scope a route under /api/admin needs. A route without it needs <see cref="ApiScope.Admin"/>.</summary>
public sealed record RequiredScope(ApiScope Scope);

/// <summary>
/// Authorization for /api/admin/*: a Bearer token (<c>Admin:ApiToken</c> or one of <c>Admin:Tokens</c>) checked against the
/// route's scope, or the web panel's session cookie (all scopes, CSRF-checked writes). The resulting <see cref="Actor"/> and
/// scopes travel in <see cref="HttpContext.Items"/>.
/// </summary>
public static class AdminAuth
{
    public const string ActorHeader = "X-Rimlink-Actor";
    public const string BadActorHeader = "X-Rimlink-Actor must be 1-64 characters without control characters";

    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, ApiScope scope) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredScope(scope));

    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(Filter);
        return group;
    }

    public static Actor Actor(this HttpContext context) =>
        context.Items[typeof(Actor)] as Actor ?? throw new InvalidOperationException("no admin actor on this request");

    public static ApiScope Scopes(this HttpContext context) => context.Items[typeof(ApiScope)] is ApiScope scopes ? scopes : ApiScope.None;

    internal static bool IsValidLabel(string value) => value.Length is >= 1 and <= 64 && !value.Any(char.IsControl);

    /// <summary>
    /// CSRF check for cookie-authenticated writes: the panel header (a cross-site form cannot set it, a cross-site fetch hits a
    /// CORS preflight nobody answers) and, when present, an <c>Origin</c> equal to this request's scheme, host and port.
    /// </summary>
    internal static bool PassesCsrf(HttpRequest request)
    {
        if (request.Headers[PanelEndpoints.PanelHeader].ToString() != "1") return false;
        string origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        string header = http.Request.Headers.Authorization.ToString();
        if (header.Length == 0 && http.Request.Cookies.ContainsKey(PanelEndpoints.CookieName))
        {
            if (http.RequestServices.GetRequiredService<PanelSessions>().Find(http.Request.Cookies[PanelEndpoints.CookieName]) is null) return Results.Unauthorized();
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !PassesCsrf(http.Request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await Run(context, next, Application.Admin.Actor.Panel, ApiScope.All);
        }

        var match = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? http.RequestServices.GetRequiredService<ApiTokens>().Find(header["Bearer ".Length..])
            : null;
        if (match is null) return Results.Unauthorized();

        string? label = null;
        if (http.Request.Headers.TryGetValue(ActorHeader, out var values))
        {
            label = values.ToString();
            if (!IsValidLabel(label)) return Results.Problem(BadActorHeader, statusCode: StatusCodes.Status400BadRequest);
        }

        var needed = http.GetEndpoint()?.Metadata.GetMetadata<RequiredScope>()?.Scope ?? ApiScope.Admin;
        if (!ApiScopes.Grants(match.Scopes, needed))
            return Results.Problem($"this needs the {ApiScopes.Name(needed)} scope", statusCode: StatusCodes.Status403Forbidden);

        return await Run(context, next, Application.Admin.Actor.ForToken(match, label), ApiScopes.Expand(match.Scopes));
    }

    private static async ValueTask<object?> Run(EndpointFilterInvocationContext context, EndpointFilterDelegate next, Actor actor, ApiScope scopes)
    {
        var http = context.HttpContext;
        http.Items[typeof(Actor)] = actor;
        http.Items[typeof(ApiScope)] = scopes;
        http.Response.Headers.CacheControl = "no-store";

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
                StatusCode.AlreadyExists => StatusCodes.Status409Conflict,
                StatusCode.ResourceExhausted => StatusCodes.Status429TooManyRequests,
                StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError,
            };
            return Results.Problem(ex.Status.Detail, statusCode: status);
        }
    }
}
