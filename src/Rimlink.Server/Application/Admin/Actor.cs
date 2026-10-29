using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Rest;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Admin;

/// <summary>Who runs an admin operation: a logged-in player (with their role), a REST API token or the web panel (account 0).</summary>
public sealed record Actor(long AccountId, long PlayerId, Role Role, string Name)
{
    public static readonly Actor Api = new(0, 0, Role.Admin, "api");

    public static readonly Actor Panel = new(0, 0, Role.Admin, "panel");

    /// <summary>The server itself (integrity checks).</summary>
    public static readonly Actor System = new(0, 0, Role.Admin, "system");

    public static Actor From(Session session) => new(session.AccountId, session.PlayerId, session.Role, session.PlayerName);

    /// <summary>
    /// A REST caller: <c>api</c>, <c>api:&lt;label&gt;</c>, <c>api:&lt;token&gt;</c> or <c>api:&lt;token&gt;:&lt;label&gt;</c>. Admin role only with the
    /// <c>admin</c> scope; the route's scope check is the boundary, the role check inside the operations a second line.
    /// </summary>
    public static Actor ForToken(ApiTokenMatch match, string? label)
    {
        string name = match.Name is null ? "api" : $"api:{match.Name}";
        if (label is not null) name += ":" + label;
        var role = ApiScopes.Grants(match.Scopes, ApiScope.Admin) ? Role.Admin : Role.Moderator;
        return new Actor(0, 0, role, name);
    }

    public void Require(Role minimum)
    {
        if (Role < minimum) throw RpcErrors.PermissionDenied($"this needs the {minimum.ToString().ToLowerInvariant()} role");
    }
}
