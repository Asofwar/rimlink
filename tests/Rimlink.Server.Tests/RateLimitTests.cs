using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Grpc;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests;

public class RateLimitTests
{
    [Fact]
    public void PolicyTableMatchesSpec()
    {
        Assert.Equal((5, TimeSpan.FromSeconds(5)), RateLimitPolicy.For("/rimlink.v1.Chat/Send"));
        Assert.Equal((1, TimeSpan.FromSeconds(30)), RateLimitPolicy.For("/rimlink.v1.Saves/Upload"));
        Assert.Equal((1, TimeSpan.FromSeconds(10)), RateLimitPolicy.For("/rimlink.v1.World/Settle"));
        Assert.Equal((10, TimeSpan.FromSeconds(60)), RateLimitPolicy.For("/rimlink.v1.Auth/Login"));
        Assert.Equal((30, TimeSpan.FromSeconds(10)), RateLimitPolicy.For("/rimlink.v1.Anything/Else"));
    }

    [Fact]
    public async Task ExceedingTheLoginLimitIsResourceExhausted()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        await fixture.RegisterAndLogin("nova");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        // Register + login already used 1 of 10 Login permits (Register is a different method).
        for (int i = 0; i < 9; i++) await auth.LoginAsync(PasswordLogin("nova", "pw"));
        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LoginAsync(PasswordLogin("nova", "pw")).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
    }

    [Fact]
    public async Task LimitsAreIndependentPerAccount()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true").WithSetting("RateLimit:Test", "2");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());

        // Logout is a generic method; RateLimit:Test caps generic methods at 2 per window. Each login replaces the previous session.
        var a1 = await fixture.RegisterAndLogin("alice");
        await auth.LogoutAsync(new Empty(), Bearer(a1.SessionToken));
        var a2 = await fixture.RegisterAndLogin("alice");
        await auth.LogoutAsync(new Empty(), Bearer(a2.SessionToken));
        var a3 = await fixture.RegisterAndLogin("alice");
        var ex = await Assert.ThrowsAsync<RpcException>(() => auth.LogoutAsync(new Empty(), Bearer(a3.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);

        var b = await fixture.RegisterAndLogin("bob");
        await auth.LogoutAsync(new Empty(), Bearer(b.SessionToken)); // bob is unaffected
    }
}
