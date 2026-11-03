using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Economy;

public sealed class RequestScopeTests
{
    [Theory]
    [InlineData("deposit")]
    [InlineData("withdraw")]
    [InlineData("post")]
    [InlineData("trade-open")]
    public async Task TheSameKeyBelongsIndependentlyToEachPlayer(string kind)
    {
        using var fixture = EconomyServer();
        var first = await fixture.RegisterAndLogin("first"); var second = await fixture.RegisterAndLogin("second"); var third = await fixture.RegisterAndLogin("third");
        await Fund(fixture, first.Player.PlayerId, 100); await Fund(fixture, second.Player.PlayerId, 100);
        async Task<long> Act(LoginReply login)
        {
            var clients = Clients(fixture, login.SessionToken);
            return kind switch
            {
                "deposit" => (await clients.Wallet.DepositAsync(new DepositRequest { ClientRequestId = "same-key", Silver = 10, GameTick = 100 }, clients.Auth)).Balance,
                "withdraw" => (await clients.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "same-key", Silver = 10 }, clients.Auth)).Id,
                "post" => (await clients.Market.PostAsync(new PostRequest { ClientRequestId = "same-key", Items = Items(("Steel", 1)), Price = 10, GameTick = 100 }, clients.Auth)).Id,
                _ => (await new Protocol.V1.Trades.TradesClient(fixture.CreateChannel()).OpenAsync(new TradeOpenRequest
                    { ClientRequestId = "same-key", PartnerId = third.Player.PlayerId, Items = Items(("Steel", 1)), Pawns = new PawnList(), GameTick = 100 }, clients.Auth)).Id,
            };
        }
        long a = await Act(first), b = await Act(second);
        if (kind == "deposit") Assert.Equal((110L, 110L), (a, b)); else Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task ChangedIntentCannotRecreditADeposit()
    {
        using var fixture = EconomyServer(); var login = await fixture.RegisterAndLogin("nova"); var client = Clients(fixture, login.SessionToken);
        var request = new DepositRequest { ClientRequestId = "key", Silver = 10, GameTick = 100 };
        await client.Wallet.DepositAsync(request, client.Auth); request.Silver = 20;
        var error = await Assert.ThrowsAsync<RpcException>(() => client.Wallet.DepositAsync(request, client.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.RequestIntentMismatch, error.ErrorDetail()?.Code);
        Assert.Equal(10, (await client.Wallet.GetAsync(new Empty(), client.Auth)).Balance);
    }

    [Fact]
    public async Task SideReplayCannotBeRedirectedToAnotherTrade()
    {
        using var fixture = EconomyServer(); var nova = await fixture.RegisterAndLogin("nova"); var bob = await fixture.RegisterAndLogin("bob");
        var api = new Protocol.V1.Trades.TradesClient(fixture.CreateChannel()); var auth = Bearer(nova.SessionToken);
        async Task<Trade> Open() => await api.OpenAsync(new TradeOpenRequest { ClientRequestId = Guid.NewGuid().ToString("N"), PartnerId = bob.Player.PlayerId,
            Items = new ItemList(), Pawns = new PawnList() }, auth);
        var first = await Open(); var second = await Open();
        var request = new TradeSideRequest { ClientRequestId = "side-key", TradeId = first.Id, Items = Items(("Steel", 1)), Pawns = new PawnList(), GameTick = 100 };
        await api.SetSideAsync(request, auth);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Transfers.Where(t => t.Kind == "trade-side").ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RequestIntent, (byte[]?)null).SetProperty(t => t.RequestTargetId, (long?)null));
        request.TradeId = second.Id;
        var error = await Assert.ThrowsAsync<RpcException>(() => api.SetSideAsync(request, auth).ResponseAsync);
        Assert.Equal(ErrorCode.RequestIntentMismatch, error.ErrorDetail()?.Code);
    }
}
