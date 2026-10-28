using Grpc.Core;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Modpack;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Auth;

/// <summary>Checks that apply after the credential is proven, in spec order: ban, modpack, world exists, player limit, server password.</summary>
public sealed class LoginGate(BanService bans, SessionRegistry sessions, WorldService worlds, ModpackService modpack, IOptions<ServerOptions> server)
{
    public async Task<LoginResult?> Check(Account account, SteamIdentity? steam, LoginContext ctx)
    {
        var ban = await bans.FindActive(account.Id, steam?.SteamId ?? account.SteamId, steam?.OwnerSteamId ?? account.OwnerSteamId, ctx.Ip);
        if (ban is not null)
        {
            string until = ban.ExpiresAt is null ? "permanently" : $"until {ban.ExpiresAt:u}";
            return new LoginResult.Failure(StatusCode.FailedPrecondition, ErrorCode.Banned, $"banned {until}: {ban.Reason}");
        }

        if (account.Role != Storage.Entities.Role.Admin && ModpackComparer.Compare(await modpack.Get(), ctx.Mods) is { } diff)
            return new LoginResult.ModpackMismatch(diff);

        // Until an admin has created the world only admins may enter (they create it).
        if (account.Role != Storage.Entities.Role.Admin && !await worlds.Exists())
            return new LoginResult.Failure(StatusCode.FailedPrecondition, ErrorCode.WorldNotCreated, "the world has not been created yet; wait for an admin");

        // The account's own old session does not count: it is replaced, not added.
        int online = sessions.All.Count(s => s.AccountId != account.Id);
        if (online >= server.Value.MaxPlayers)
            return new LoginResult.Failure(StatusCode.FailedPrecondition, ErrorCode.ServerFull, $"server is full ({server.Value.MaxPlayers} players)");

        string? required = server.Value.ServerPassword;
        if (!string.IsNullOrEmpty(required) && ctx.ServerPassword != required)
            return new LoginResult.Failure(StatusCode.FailedPrecondition, ErrorCode.WrongPassword, "wrong server password");

        return null;
    }
}
