using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Economy;

public class AntiDupeTests
{
    [Fact]
    public async Task UploadOlderThanLastOutgoingTickIsSaveStale()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1)), tick: 500), n.Auth);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());

        byte[] old = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 400);
        var ex = await Assert.ThrowsAsync<RpcException>(() => Upload(saves, nova.SessionToken, old, HeaderFor(old, ServerFixture.WorldSeed, "Nova", tick: 400)));
        Assert.Equal(ErrorCode.SaveStale, ex.ErrorDetail()?.Code);
        Assert.Empty((await saves.ListAsync(new Empty(), n.Auth)).Versions);

        byte[] fresh = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 500);
        var info = await Upload(saves, nova.SessionToken, fresh, HeaderFor(fresh, ServerFixture.WorldSeed, "Nova", tick: 500));
        Assert.Equal(1, info.Version);
    }

    [Fact]
    public async Task RollbackBelowTheOutgoingTickResetsItAndLogs()
    {
        using var fixture = EconomyServer().WithSetting("Auth:AdminLogins:0", "admin");
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] v1 = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 300);
        await Upload(saves, nova.SessionToken, v1, HeaderFor(v1, ServerFixture.WorldSeed, "Nova", tick: 300));
        byte[] v2 = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 600, paddingBytes: 10);
        await Upload(saves, nova.SessionToken, v2, HeaderFor(v2, ServerFixture.WorldSeed, "Nova", tick: 600));
        var n = Clients(fixture, nova.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1)), tick: 600), n.Auth);

        using var scope = fixture.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<AdminOperations>();
        Assert.True(await ops.RollbackSave(new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin"), nova.Player.PlayerId, 1));

        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(300, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == nova.Player.PlayerId)).LastOutgoingTick);
        Assert.Contains("given", (await db.AdminActions.OrderByDescending(a => a.Id).FirstAsync()).Details);
    }

    [Fact]
    public async Task FrozenEconomyRefusesTradesButAllowsDelivery()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth);

        var freeze = fixture.Services.GetRequiredService<FreezeState>();
        freeze.Frozen = true;
        freeze.Reason = "maintenance";

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.EconomyFrozen, ex.ErrorDetail()?.Code);
        Assert.Contains("maintenance", ex.Status.Detail);
        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 1 }, b.Auth);
    }

    [Fact]
    public async Task DisabledEconomyRefusesEverything()
    {
        using var fixture = EconomyServer().WithSetting("Economy:Enabled", "false");
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Wallet.GetAsync(new Empty(), n.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.EconomyFrozen, ex.ErrorDetail()?.Code);
    }
}
