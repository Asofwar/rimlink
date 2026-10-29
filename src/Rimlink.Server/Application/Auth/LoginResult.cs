using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Application.Auth;

/// <summary>What the client sent with the login request, beyond the credential itself.</summary>
public sealed record LoginContext(int ProtocolVersion, string? ServerPassword, IReadOnlyList<ModRef> Mods, string Ip)
{
    /// <summary>The settings file hashes of the client's Mod classes (newer clients); empty = settings are not compared.</summary>
    public IReadOnlyList<ModSettingsHash> ModSettings { get; init; } = [];

    public static LoginContext From(LoginRequest request, string ip) =>
        new(request.ProtocolVersion, request.ServerPassword, request.Mods, ip) { ModSettings = request.ModSettings };
}

public abstract record LoginResult
{
    public sealed record Success(Session Session, Account Account, Player Player) : LoginResult;

    public sealed record Failure(StatusCode Status, ErrorCode Code, string Key, string Message, params object[] Args) : LoginResult;

    /// <summary>Credentials were fine but the mods differ; the client gets the diff instead of a token.</summary>
    public sealed record ModpackMismatch(ModpackDiff Diff) : LoginResult;

    public static LoginResult Failed(StatusCode status, ErrorCode code, string key, string message, params object[] args) => new Failure(status, code, key, message, args);
}
