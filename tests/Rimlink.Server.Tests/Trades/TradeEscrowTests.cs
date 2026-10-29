using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Trades;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SocialFixtures;

namespace Rimlink.Server.Tests.Trades;

/// <summary>Where each side's goods are after a trade closes: never in two places, never nowhere.</summary>
public class TradeEscrowTests
{
    private sealed record Party(long Id, SocialClients C, Metadata Auth);

    private static async Task<Party> Join(ServerFixture fixture, string login, string name)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: name);
        await Fund(fixture, reply.Player.PlayerId, 100);
        return new Party(reply.Player.PlayerId, SocialFixtures.Social(fixture, reply.SessionToken), Bearer(reply.SessionToken));
    }

    private static async Task<long> Balance(Party p) => (await p.C.Economy.Wallet.GetAsync(new Empty(), p.Auth)).Balance;

    private static async Task<List<Parcel>> Mailbox(Party p) => (await p.C.Economy.Mailbox.ListAsync(new Empty(), p.Auth)).Parcels.ToList();

    /// <summary>Nova offers steel, a pawn and 30 silver; Bob answers with plasteel and 20 silver.</summary>
    private static async Task<Trade> TwoSided(Party nova, Party bob)
    {
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), Pawns(("Colonist", "Ann")), silver: 30), nova.Auth);
        return await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5)), silver: 20, baseRevision: trade.Revision), bob.Auth);
    }

    private static async Task AssertBothBack(Party nova, Party bob)
    {
        var toNova = Assert.Single(await Mailbox(nova));
        var toBob = Assert.Single(await Mailbox(bob));
        Assert.Equal(ParcelKind.ParcelTradeReturn, toNova.Kind);
        Assert.Equal("Steel", toNova.Items.Items.Single().DefName);
        Assert.Equal("Ann", toNova.Pawns.Pawns.Single().Name);
        Assert.Equal(0, toNova.Silver);
        Assert.Equal("Plasteel", toBob.Items.Items.Single().DefName);
        Assert.Equal(100, await Balance(nova));
        Assert.Equal(100, await Balance(bob));
    }

    [Fact]
    public async Task CancelReturnsBothSides()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await TwoSided(nova, bob);
        double cancelled = TradeMetrics.Outcomes.WithLabels("cancelled").Value;

        var closed = await bob.C.Trades.CancelAsync(new TradeRef { TradeId = trade.Id }, bob.Auth);
        var again = await nova.C.Trades.CancelAsync(new TradeRef { TradeId = trade.Id }, nova.Auth);

        Assert.Equal(TradeStatus.TradeCancelled, closed.Status);
        Assert.Contains("Bob", closed.CloseReason);
        Assert.Equal(TradeStatus.TradeCancelled, again.Status);
        await AssertBothBack(nova, bob);
        var confirm = await Assert.ThrowsAsync<RpcException>(() => nova.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = closed.Revision }, nova.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.TradeGone, confirm.ErrorDetail()?.Code);
        Assert.Equal(cancelled + 1, TradeMetrics.Outcomes.WithLabels("cancelled").Value);
    }

    [Fact]
    public async Task ExpiredTradesReturnBothSides()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await TwoSided(nova, bob);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Trades.Where(t => t.Id == trade.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

        int expired = await scope.ServiceProvider.GetRequiredService<TradeService>().ExpireDue(DateTime.UtcNow);

        Assert.Equal(1, expired);
        Assert.Equal(TradeStatus.TradeExpired, Assert.Single((await nova.C.Trades.ListAsync(new Empty(), nova.Auth)).Trades).Status);
        await AssertBothBack(nova, bob);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<TradeService>().ExpireDue(DateTime.UtcNow));
    }

    [Fact]
    public async Task SetSideRepeatAfterTheTradeClosedReturnsTheTradeAndMovesNothing()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10))), nova.Auth);
        var request = TradeSide(trade.Id, Items(("Plasteel", 5)), silver: 20, baseRevision: trade.Revision);
        var bobSide = await bob.C.Trades.SetSideAsync(request, bob.Auth);
        await nova.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = bobSide.Revision }, nova.Auth);

        var replay = await bob.C.Trades.SetSideAsync(request, bob.Auth);

        Assert.Equal(TradeStatus.TradeCompleted, replay.Status);
        Assert.Equal(80, await Balance(bob));
        using (var scope = fixture.Services.CreateScope())
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.CountAsync());
        var fresh = await Assert.ThrowsAsync<RpcException>(() => bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Gold", 1))), bob.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.TradeGone, fresh.ErrorDetail()?.Code);
    }

    [Fact]
    public async Task SetSideRepeatAfterACancelMovesNothing()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10))), nova.Auth);
        var request = TradeSide(trade.Id, Items(("Plasteel", 5)), silver: 20);
        await bob.C.Trades.SetSideAsync(request, bob.Auth);
        await nova.C.Trades.CancelAsync(new TradeRef { TradeId = trade.Id }, nova.Auth);

        var replay = await bob.C.Trades.SetSideAsync(request, bob.Auth);

        Assert.Equal(TradeStatus.TradeCancelled, replay.Status);
        Assert.Equal("Plasteel", Assert.Single(await Mailbox(bob)).Items.Items.Single().DefName);
        Assert.Equal(100, await Balance(bob));
    }
}
