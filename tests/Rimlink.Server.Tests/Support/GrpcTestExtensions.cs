using Grpc.Core;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Server.Tests.Support;

public static class GrpcTestExtensions
{
    public static Metadata Bearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    public static ErrorDetail? ErrorDetail(this RpcException ex)
    {
        var entry = ex.Trailers.FirstOrDefault(t => t.Key == "rimlink-error-bin");
        return entry is null ? null : Protocol.V1.ErrorDetail.Parser.ParseFrom(entry.ValueBytes);
    }

    public static LoginRequest PasswordLogin(string login, string password, int protocolVersion = ProtocolVersion.Current) => new()
    {
        ProtocolVersion = protocolVersion,
        Password = new PasswordCredential { Login = login, Password = password },
    };

    /// <summary>Registers (if needed) and logs in a password account; returns the reply with its token.</summary>
    public static async Task<LoginReply> RegisterAndLogin(this ServerFixture fixture, string login, string password = "pw", string? playerName = null)
    {
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());
        try
        {
            await auth.RegisterAsync(new RegisterRequest { Login = login, Password = password, PlayerName = playerName ?? login });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }
        return await auth.LoginAsync(PasswordLogin(login, password));
    }
}
