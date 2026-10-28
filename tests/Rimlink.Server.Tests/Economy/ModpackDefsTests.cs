using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Economy;

public class ModpackDefsTests
{
    private static ServerFixture Server() => EconomyServer().WithSetting("Auth:AdminLogins:0", "admin");

    [Fact]
    public async Task SendWithAnUnknownDefIsRefusedOnceDefsAreSet()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Plasteel", 1))), n.Auth); // check off: anything passes

        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());
        await modpack.SetDefsAsync(new DefList { DefNames = { "Steel" } }, Bearer(admin.SessionToken));

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Plasteel", 1))), n.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.DefNotInModpack, ex.ErrorDetail()?.Code);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth);
    }

    [Fact]
    public async Task SetDefsRequiresAdmin()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var modpack = new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => modpack.SetDefsAsync(new DefList { DefNames = { "Steel" } }, Bearer(nova.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task DefsSurviveARestart()
    {
        string dataDir;
        using (var fixture = Server())
        {
            dataDir = fixture.DataDir;
            var admin = await fixture.RegisterAndLogin("admin");
            await new Protocol.V1.Modpack.ModpackClient(fixture.CreateChannel()).SetDefsAsync(new DefList { DefNames = { "Steel" } }, Bearer(admin.SessionToken));
        }
        using var again = Server().WithSetting("Server:DataDir", dataDir);
        var nova = await again.RegisterAndLogin("nova");
        var bob = await again.RegisterAndLogin("bob");
        var n = Clients(again, nova.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Plasteel", 1))), n.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.DefNotInModpack, ex.ErrorDetail()?.Code);
    }
}
