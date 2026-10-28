using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.World;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Grpc;

public sealed class AuthGrpcService(AuthService auth, WorldService worlds) : Protocol.V1.Auth.AuthBase
{
    public override async Task<LoginReply> Login(LoginRequest request, ServerCallContext context)
    {
        var ctx = LoginContext.From(request, context.ClientIp());
        LoginResult result = request.CredentialCase switch
        {
            LoginRequest.CredentialOneofCase.Password => await auth.LoginWithPassword(request.Password.Login, request.Password.Password, ctx),
            LoginRequest.CredentialOneofCase.SteamTicket => await auth.LoginWithSteam(request.SteamTicket.ToByteArray(), ctx),
            _ => LoginResult.Failed(StatusCode.InvalidArgument, ErrorCode.Unspecified, "no supported credential in request"),
        };
        return await ToReply(result, context);
    }

    public override async Task<LoginReply> Register(RegisterRequest request, ServerCallContext context)
    {
        var ctx = new LoginContext(Protocol.ProtocolVersion.Current, null, [], context.ClientIp());
        return await ToReply(await auth.Register(request.Login, request.Password, request.PlayerName, ctx), context);
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
                throw RpcErrors.With(f.Status, f.Code, f.Message);
            case LoginResult.Success s:
                UnauthenticatedConnectionLimiter.MarkAuthenticated(context.GetHttpContext());
                return new LoginReply
                {
                    SessionToken = s.Session.Token,
                    Player = s.Player.ToRef(),
                    Role = (Protocol.V1.Role)s.Account.Role,
                    WorldExists = await worlds.Exists(),
                    HasSettlement = await worlds.HasSettlement(s.Player.Id),
                };
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
