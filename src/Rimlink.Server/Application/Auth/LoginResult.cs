using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Auth;

/// <summary>What the client sent with the login request, beyond the credential itself.</summary>
public sealed record LoginContext(int ProtocolVersion, string? ServerPassword, IReadOnlyList<ModRef> Mods, string Ip)
{
    public static LoginContext From(LoginRequest request, string ip) =>
        new(request.ProtocolVersion, request.ServerPassword, request.Mods, ip);
}

public abstract record LoginResult
{
    public sealed record Success(Session Session, Account Account, Player Player) : LoginResult;

    public sealed record Failure(StatusCode Status, ErrorCode Code, string Message) : LoginResult;

    /// <summary>Credentials were fine but the mods differ; the client gets the diff instead of a token.</summary>
    public sealed record ModpackMismatch(ModpackDiff Diff) : LoginResult;

    public static LoginResult Failed(StatusCode status, ErrorCode code, string message) => new Failure(status, code, message);
}
