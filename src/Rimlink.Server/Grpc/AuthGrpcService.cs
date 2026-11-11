using Grpc.Core;
using Rimlink.Server.Application.GuildCapture;
using Rimlink.Server.Application.Outposts;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Grpc;

public sealed class AuthGrpcService(AuthService auth, WorldService worlds, SaveStore saves, SessionRegistry sessions, RimlinkDbContext db, Application.Live.LiveSessionService live, Microsoft.Extensions.Options.IOptions<IntegrityOptions> integrity,
    Application.World.ColonyArchive archive, Application.Outposts.OutpostFeature outposts) : Protocol.V1.Auth.AuthBase
{
    public override async Task<LoginReply> Login(LoginRequest request, ServerCallContext context)
    {
        GuildRuntimeNegotiation.Require(outposts,request.GuildRuntime);
        var ctx = LoginContext.From(request, context.ClientIp()) with { Cancellation = context.CancellationToken };
        LoginResult result = request.CredentialCase switch
        {
            LoginRequest.CredentialOneofCase.Password => await auth.LoginWithPassword(request.Password.Login, request.Password.Password, ctx),
            LoginRequest.CredentialOneofCase.SteamTicket => await auth.LoginWithSteam(request.SteamTicket.ToByteArray(), ctx),
            _ => LoginResult.Failed(StatusCode.InvalidArgument, ErrorCode.Unspecified, RefusalKeys.AuthNoCredential, "no supported credential in request"),
        };
        return await ToReply(result, context);
    }

    public override async Task<LoginReply> Register(RegisterRequest request, ServerCallContext context)
    {
        GuildRuntimeNegotiation.Require(outposts,request.GuildRuntime);
        var ctx = new LoginContext(Protocol.ProtocolVersion.Current, request.ServerPassword, request.Mods, context.ClientIp()) { ModSettings = request.ModSettings, RecoveryVersion = request.RecoveryVersion, OutpostVersion = request.OutpostVersion, GuildVersion = request.GuildVersion, Cancellation = context.CancellationToken };
        return await ToReply(await auth.Register(request.Login, request.Password, request.PlayerName, ctx), context);
    }

    public override Task<Empty> SetAtHome(AtHomeRequest request, ServerCallContext context)
    {
        context.GetSession().AtHome = request.AtHome;
        context.GetSession().CoopCapable = request.CoopCapable;
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> Logout(Empty request, ServerCallContext context)
    {
        auth.Logout(context.GetSession());
        return Task.FromResult(new Empty());
    }

    private async Task<LoginReply> ToReply(LoginResult result, ServerCallContext context)
    {
        switch (result)
        {
            case LoginResult.Failure f:
                throw RpcErrors.With(f.Status, f.Code, f.Key, f.Message, f.Args);
            case LoginResult.ModpackMismatch m:
                return new LoginReply { ModpackDiff = m.Diff, GuildRuntime=GuildRuntimeNegotiation.Target(outposts) };
            case LoginResult.Success s:
                UnauthenticatedConnectionLimiter.MarkAuthenticated(context.GetHttpContext());
                // Before HasSettlement/HasSave: an archived colony that comes back makes this an ordinary login.
                s.Session.ColonyRestore = await archive.RestoreOnLogin(s.Player.Id);
                var reply = new LoginReply
                {
                    SessionToken = s.Session.Token,
                    Player = s.Player.ToRef(),
                    Role = (Protocol.V1.Role)s.Account.Role,
                    WorldExists = await worlds.Exists(),
                    HasSettlement = await worlds.HasSettlement(s.Player.Id),
                    HasSave = await saves.HasSave(s.Player.Id),
                    SaveGeneration = await db.Players.Where(p => p.Id == s.Player.Id).Select(p => p.SaveGeneration).SingleAsync(),
                    SaveWriteEpoch = await db.Players.Where(p => p.Id == s.Player.Id).Select(p => p.SaveWriteEpoch).SingleAsync(),
                    RecoveryVersion = Protocol.SaveRecoveryData.CurrentVersion,
                    DevModeAllowed = integrity.Value.DevModeAllowed(s.Account.Role),
                    ColonyRestore = s.Session.ColonyRestore,
                    GuildRuntime = GuildRuntimeNegotiation.Target(outposts),
                    OutpostIsolatedAcceptance = outposts.Readiness.Mode == OutpostRuntimeMode.IsolatedAcceptance,
                    OutpostVersion = outposts.IsAvailable
                        && s.Session.OutpostVersion == Protocol.OutpostRecoveryData.CurrentVersion ? Protocol.OutpostRecoveryData.CurrentVersion : 0,
                };
                if (s.GuildRestoreBoundary is { } guildBoundary)
                {
                    reply.GuildVersion = Protocol.GuildResourceContracts.CurrentVersion;
                    reply.OutpostRestoreBoundary = guildBoundary;
                }
                else if (reply.OutpostVersion != 0)
                    reply.OutpostRestoreBoundary = await db.OutpostAuthority.Select(a => a.RestoreBoundary).SingleAsync();
                var onlineIds = sessions.All.Select(x => x.PlayerId).Distinct().ToList();
                reply.Online.AddRange((await db.Players.AsNoTracking().Where(p => onlineIds.Contains(p.Id)).ToListAsync()).Select(p => p.ToRef()));
                foreach (var pending in await live.PendingEnds(s.Player.Id)) reply.PendingLiveEnds.Add(await live.ToProto(pending));
                return reply;
            default:
                throw new InvalidOperationException("unknown login result");
        }
    }
}

public static class PlayerExtensions
{
    public static PlayerRef ToRef(this Player player) => new()
    {
        PlayerId = player.Id,
        Name = player.Name,
        FactionName = player.FactionName,
        ColorArgb = player.ColorArgb,
    };
}
