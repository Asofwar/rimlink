using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Auth;

public class PresenceTests
{
    [Fact]
    public async Task SetAtHomeIsVisibleToTheRegistry()
    {
        using var fixture = new ServerFixture().WithSetting("Auth:AllowPassword", "true");
        var nova = await fixture.RegisterAndLogin("nova");
        var auth = new Protocol.V1.Auth.AuthClient(fixture.CreateChannel());
        var registry = fixture.Services.GetRequiredService<SessionRegistry>();
        Assert.False(registry.ByPlayer(nova.Player.PlayerId)!.AtHome);

        await auth.SetAtHomeAsync(new AtHomeRequest { AtHome = true }, Bearer(nova.SessionToken));
        Assert.True(registry.ByPlayer(nova.Player.PlayerId)!.AtHome);

        await auth.SetAtHomeAsync(new AtHomeRequest { AtHome = false }, Bearer(nova.SessionToken));
        Assert.False(registry.ByPlayer(nova.Player.PlayerId)!.AtHome);

        await auth.SetAtHomeAsync(new AtHomeRequest { AtHome = true }, Bearer(nova.SessionToken));
        await auth.LogoutAsync(new Empty(), Bearer(nova.SessionToken));
        Assert.Null(registry.ByPlayer(nova.Player.PlayerId)); // presence goes with the session
    }
}
