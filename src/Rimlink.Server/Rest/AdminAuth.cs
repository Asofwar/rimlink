using Grpc.Core;
using Rimlink.Server.Application.Admin;

namespace Rimlink.Server.Rest;

/// <summary>Endpoint metadata: the API scope a route under /api/admin needs. A route without it needs <see cref="ApiScope.Admin"/>.</summary>
public sealed record RequiredScope(ApiScope Scope);

/// <summary>
/// Authorization for /api/admin/*: a Bearer token (<c>Admin:ApiToken</c> or one of <c>Admin:Tokens</c>) checked against the
/// route's scope. The resulting <see cref="Actor"/> and scopes travel in <see cref="HttpContext.Items"/>.
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

    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        string header = http.Request.Headers.Authorization.ToString();
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

        http.Items[typeof(Actor)] = Application.Admin.Actor.ForToken(match, label);
        http.Items[typeof(ApiScope)] = ApiScopes.Expand(match.Scopes);
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
