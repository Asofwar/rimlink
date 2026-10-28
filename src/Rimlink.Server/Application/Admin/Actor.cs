using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Application.Admin;

/// <summary>Who runs an admin operation: a logged-in player (with their role) or the REST API token (admin, account 0).</summary>
public sealed record Actor(long AccountId, long PlayerId, Role Role, string Name)
{
    public static readonly Actor Api = new(0, 0, Role.Admin, "api");

    public static Actor From(Session session) => new(session.AccountId, session.PlayerId, session.Role, session.PlayerName);

    public void Require(Role minimum)
    {
        if (Role < minimum) throw RpcErrors.PermissionDenied($"this needs the {minimum.ToString().ToLowerInvariant()} role");
    }
}
