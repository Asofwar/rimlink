using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Auth;

public class PasswordLoginTests
{
    private static ServerFixture PasswordServer() => new ServerFixture().WithSetting("Auth:AllowPassword", "true");

    [Fact]
    public async Task RegisterThenLoginReturnsTokenAndPlayer()
    {
        using var fixture = PasswordServer();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var registered = await auth.RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" });
        var loggedIn = await auth.LoginAsync(PasswordLogin("nova", "pw"));

        Assert.NotEmpty(registered.SessionToken);
        Assert.NotEmpty(loggedIn.SessionToken);
        Assert.NotEqual(registered.SessionToken, loggedIn.SessionToken);
        Assert.Equal("Nova", loggedIn.Player.Name);
        Assert.Equal(Role.Player, loggedIn.Role);
        Assert.False(loggedIn.WorldExists);
    }

    [Fact]
    public async Task WrongPasswordIsUnauthenticated()
    {
        using var fixture = PasswordServer();
        await fixture.RegisterAndLogin("nova", "pw");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("nova", "wrong")).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task RegisterIsDisabledByDefault()
    {
        using var fixture = new ServerFixture();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            auth.RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "Nova" }).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task DuplicateLoginIsAlreadyExists()
    {
        using var fixture = PasswordServer();
        await fixture.RegisterAndLogin("nova", "pw");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            auth.RegisterAsync(new RegisterRequest { Login = "NOVA", Password = "other", PlayerName = "Nova2" }).ResponseAsync);

        Assert.Equal(StatusCode.AlreadyExists, ex.StatusCode);
    }

    [Fact]
    public async Task InvalidPlayerNameIsRejectedOnRegister()
    {
        using var fixture = PasswordServer();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            auth.RegisterAsync(new RegisterRequest { Login = "nova", Password = "pw", PlayerName = "../x" }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(ErrorCode.NameInvalid, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task CallWithoutTokenIsUnauthenticated()
    {
        using var fixture = PasswordServer();
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LogoutAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task LogoutInvalidatesToken()
    {
        using var fixture = PasswordServer();
        var reply = await fixture.RegisterAndLogin("nova");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        await auth.LogoutAsync(new Empty(), Bearer(reply.SessionToken));
        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LogoutAsync(new Empty(), Bearer(reply.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task OldProtocolVersionIsRejected()
    {
        using var fixture = PasswordServer();
        await fixture.RegisterAndLogin("nova");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("nova", "pw", protocolVersion: 0)).ResponseAsync);

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(ErrorCode.ProtocolVersion, ex.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task AdminLoginFromConfigGetsAdminRole()
    {
        using var fixture = PasswordServer().WithSetting("Auth:AdminLogins:0", "boss");

        var reply = await fixture.RegisterAndLogin("boss");

        Assert.Equal(Role.Admin, reply.Role);
    }
}
