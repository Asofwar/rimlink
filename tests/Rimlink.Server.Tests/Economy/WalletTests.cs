using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;

namespace Rimlink.Server.Tests.Economy;

public class WalletTests
{
    [Fact]
    public async Task DepositCreditsAndStampsTheTick()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);

        var state = await n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "dep-1", Silver = 50, GameTick = 700 }, n.Auth);
        var again = await n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "dep-1", Silver = 50, GameTick = 700 }, n.Auth);

        Assert.Equal(50, state.Balance);
        Assert.Equal(50, again.Balance);
        Assert.Equal(50, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(700, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == nova.Player.PlayerId)).LastOutgoingTick);
    }

    [Fact]
    public async Task WithdrawDebitsAndMailsPhysicalSilver()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        await Fund(fixture, nova.Player.PlayerId, 200);
        var n = Clients(fixture, nova.SessionToken);

        var parcel = await n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "wd-1", Silver = 150 }, n.Auth);
        var same = await n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "wd-1", Silver = 150 }, n.Auth);

        Assert.Equal(parcel.Id, same.Id);
        Assert.Equal(ParcelKind.ParcelWithdraw, parcel.Kind);
        Assert.Equal(150, parcel.Silver);
        Assert.Equal(50, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        await n.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 5 }, n.Auth);
        Assert.Equal(50, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
    }

    [Fact]
    public async Task WithdrawMoreThanBalanceIsWalletInsufficient()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "wd-2", Silver = 10 }, n.Auth).ResponseAsync);

        Assert.Equal(ErrorCode.WalletInsufficient, ex.ErrorDetail()?.Code);
        Assert.Empty((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
    }

    [Fact]
    public async Task HistoryListsEntriesNewestFirst()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        await n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "a", Silver = 10, GameTick = 1 }, n.Auth);
        await n.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "b", Silver = 20, GameTick = 2 }, n.Auth);

        var history = await n.Wallet.HistoryAsync(new Empty(), n.Auth);

        Assert.Equal([20, 10], history.Entries.Select(e => e.Delta));
        Assert.All(history.Entries, e => Assert.Equal("deposit", e.Kind));
    }
}
