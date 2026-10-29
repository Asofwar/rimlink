using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Storage;

namespace Rimlink.Server.Application.Auth;

public sealed class AuthService(
    RimlinkDbContext db,
    SessionRegistry sessions,
    ISteamTicketVerifier steam,
    LoginGate gate,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthService> log)
{
    public async Task<LoginResult> Register(string login, string password, string playerName, LoginContext ctx)
    {
        if (!authOptions.Value.AllowPassword)
            return LoginResult.Failed(StatusCode.PermissionDenied, ErrorCode.Unspecified, RefusalKeys.AuthPasswordDisabled, "password accounts are disabled on this server");
        if (!Names.IsValid(login))
            return LoginResult.Failed(StatusCode.InvalidArgument, ErrorCode.NameInvalid, RefusalKeys.AuthLoginInvalid, "login is not a valid name");
        if (!Names.IsValid(playerName))
            return LoginResult.Failed(StatusCode.InvalidArgument, ErrorCode.NameInvalid, RefusalKeys.AuthPlayerNameInvalid, "player name is not a valid name");
        if (string.IsNullOrEmpty(password))
            return LoginResult.Failed(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.AuthPasswordEmpty, "password is empty");
        if (await db.Accounts.AnyAsync(a => a.Login == login))
            return LoginResult.Failed(StatusCode.AlreadyExists, ErrorCode.Unspecified, RefusalKeys.AuthLoginTaken, "login already taken");
        if (await db.Players.AnyAsync(p => p.Name == playerName))
            return LoginResult.Failed(StatusCode.AlreadyExists, ErrorCode.Unspecified, RefusalKeys.AuthPlayerNameTaken, "player name already taken");

        var account = new Account
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = authOptions.Value.AdminLogins.Contains(login, StringComparer.OrdinalIgnoreCase) ? Storage.Entities.Role.Admin : Storage.Entities.Role.Player,
            CreatedAt = DateTime.UtcNow,
        };
        account.Player = new Player { Account = account, Name = playerName, FactionName = playerName };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        log.LogInformation("Registered account {Login} ({AccountId})", login, account.Id);

        return await Complete(account, ctx);
    }

    public async Task<LoginResult> LoginWithPassword(string login, string password, LoginContext ctx)
    {
        if (!authOptions.Value.AllowPassword)
            return LoginResult.Failed(StatusCode.PermissionDenied, ErrorCode.Unspecified, RefusalKeys.AuthPasswordDisabled, "password accounts are disabled on this server");

        var account = await db.Accounts.Include(a => a.Player).FirstOrDefaultAsync(a => a.Login == login);
        if (account?.PasswordHash is null || !PasswordHasher.Verify(password, account.PasswordHash))
            return LoginResult.Failed(StatusCode.Unauthenticated, ErrorCode.Unspecified, RefusalKeys.AuthInvalidCredentials, "login details are invalid");

        return await Complete(account, ctx);
    }

    public async Task<LoginResult> LoginWithSteam(byte[] ticket, LoginContext ctx)
    {
        var identity = await steam.Verify(ticket, CancellationToken.None);
        if (identity is null)
            return LoginResult.Failed(StatusCode.Unauthenticated, ErrorCode.Unspecified, RefusalKeys.AuthSteamTicketInvalid, "Steam ticket is not valid");

        var account = await db.Accounts.Include(a => a.Player).FirstOrDefaultAsync(a => a.SteamId == identity.SteamId);
        if (account is null)
        {
            account = new Account
            {
                SteamId = identity.SteamId,
                Role = authOptions.Value.AdminSteamIds.Contains(identity.SteamId) ? Storage.Entities.Role.Admin : Storage.Entities.Role.Player,
                CreatedAt = DateTime.UtcNow,
            };
            string name = await FreeName("Player" + identity.SteamId.ToString()[^4..]);
            account.Player = new Player { Account = account, Name = name, FactionName = name };
            db.Accounts.Add(account);
            log.LogInformation("Registered Steam account {SteamId} ({Name})", identity.SteamId, name);
        }
        account.OwnerSteamId = identity.OwnerSteamId;
        await db.SaveChangesAsync();

        return await Complete(account, ctx, identity);
    }

    /// <summary>Player names are unique (case-insensitive); the generated Steam name gets a suffix when taken.</summary>
    private async Task<string> FreeName(string wanted)
    {
        string candidate = wanted;
        for (int i = 2; await db.Players.AnyAsync(p => p.Name == candidate); i++) candidate = $"{wanted}-{i}";
        return candidate;
    }

    public void Logout(Session session)
    {
        sessions.End(session.Token);
        log.LogInformation("Logout {Player} ({AccountId})", session.PlayerName, session.AccountId);
    }

    /// <summary>Shared tail of every credential path: gate checks, session start, bookkeeping.</summary>
    private async Task<LoginResult> Complete(Account account, LoginContext ctx, SteamIdentity? steamIdentity = null)
    {
        if (ctx.ProtocolVersion != Protocol.ProtocolVersion.Current)
            return LoginResult.Failed(StatusCode.FailedPrecondition, ErrorCode.ProtocolVersion,
                RefusalKeys.AuthProtocolVersion, $"server speaks protocol {Protocol.ProtocolVersion.Current}, client sent {ctx.ProtocolVersion}", Protocol.ProtocolVersion.Current, ctx.ProtocolVersion);

        if (await gate.Check(account, steamIdentity, ctx) is { } refused) return refused;

        var player = account.Player ?? await db.Players.FirstAsync(p => p.AccountId == account.Id);
        var session = sessions.Start(account, player, ctx.Ip, out var replaced);
        if (replaced is not null) log.LogInformation("Session of {Player} replaced by a new login from {Ip}", player.Name, ctx.Ip);

        account.LastSeenAt = DateTime.UtcNow;
        player.LastIp = ctx.Ip;
        db.Sessions.Add(new SessionLog { AccountId = account.Id, Ip = ctx.Ip, StartedAt = session.StartedAt });
        await db.SaveChangesAsync();

        log.LogInformation("Login {Player} ({AccountId}) from {Ip}", player.Name, account.Id, ctx.Ip);
        return new LoginResult.Success(session, account, player);
    }
}
