using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.IncidentFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;
using static Rimlink.Server.Tests.Support.SocialFixtures;
using Raid = Rimlink.Server.Storage.Entities.Raid;
using Trade = Rimlink.Protocol.V1.Trade;

namespace Rimlink.Server.Tests.Trades;

/// <summary>A trade is a way out of a colony too: the economy hardening rules hold for it (economy hardening spec §10).</summary>
public class TradeHardeningTests
{
    private sealed record Party(long Id, SocialClients C, Metadata Auth);

    private static ServerFixture Server() => RaidServer().WithSetting("Economy:StorageFreeDays", "0");

    private static async Task<Party> Join(ServerFixture fixture, string login, string name)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: name);
        await Fund(fixture, reply.Player.PlayerId, 100);
        return new Party(reply.Player.PlayerId, SocialFixtures.Social(fixture, reply.SessionToken), Bearer(reply.SessionToken));
    }

    private static async Task<long> WealthOf(ServerFixture fixture, Party p)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ServerWealth>().Of(p.Id);
    }

    private static async Task<long> Balance(Party p) => (await p.C.Economy.Wallet.GetAsync(new Empty(), p.Auth)).Balance;

    private static void Forbid(ServerFixture fixture, params string[] defs) => fixture.Services.GetRequiredService<ForbiddenDefs>().Replace(defs);

    private static async Task SeedRaid(ServerFixture fixture, long attacker, long target)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        db.Raids.Add(new Raid
        {
            AttackerPlayerId = attacker, TargetPlayerId = target, State = RaidStateDb.Queued, Controller = (int)RaidController.ControllerAi,
            Pawns = Pawns(("Colonist", "Raider")).ToByteArray(), CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
    }

    private static ItemList Worth(int count, long silverEach)
    {
        var items = Items(("Steel", count));
        foreach (var item in items.Items) item.MarketValueX100 = silverEach * 100;
        return items;
    }

    private static PawnList PawnWorth(long silver)
    {
        var pawns = Pawns(("Colonist", "Ann"));
        pawns.Pawns[0].MarketValueX100 = silver * 100;
        return pawns;
    }

    private static async Task<ErrorCode?> Refused(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(call);
        return CodeOf(ex);
    }

    private static async Task Complete(Party nova, Party bob, Trade trade)
    {
        await bob.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = trade.Revision }, bob.Auth);
        await nova.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = trade.Revision }, nova.Auth);
    }

    [Fact]
    public async Task ForbiddenDefsDoNotEnterATrade()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        Forbid(fixture, "Steel");

        Assert.Equal(ErrorCode.DefForbidden, await Refused(() => nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 5))), nova.Auth).ResponseAsync));

        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Plasteel", 5))), nova.Auth);
        Assert.Equal(ErrorCode.DefForbidden, await Refused(() => nova.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Steel", 5))), nova.Auth).ResponseAsync));
    }

    [Fact]
    public async Task GoodsAlreadyInATradeSurviveANewBan()
    {
        // The list is changed by an admin at any time: what is already in escrow must still be able to move or go back.
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 5))), nova.Auth);
        Forbid(fixture, "Steel");

        var changed = await nova.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5))), nova.Auth);

        await Complete(nova, bob, changed);
        var parcel = Assert.Single((await bob.C.Economy.Mailbox.ListAsync(new Empty(), bob.Auth)).Parcels);
        Assert.Equal(["Steel", "Plasteel"], parcel.Items.Items.Select(i => i.DefName).ToArray());
    }

    [Fact]
    public async Task AColonyUnderAttackMovesNothingIntoATrade()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await bob.C.Trades.OpenAsync(OpenTrade(nova.Id, Items(("Steel", 5)), silver: 10), bob.Auth);
        await SeedRaid(fixture, nova.Id, bob.Id);

        Assert.Equal(ErrorCode.UnderThreat, await Refused(() => bob.C.Trades.OpenAsync(OpenTrade(nova.Id, Items(("Steel", 5))), bob.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.UnderThreat, await Refused(() => bob.C.Trades.OpenAsync(OpenTrade(nova.Id, silver: 5), bob.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.UnderThreat, await Refused(() => bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Steel", 5)), silver: 10), bob.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.UnderThreat, await Refused(() => bob.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 20), bob.Auth).ResponseAsync));
    }

    [Fact]
    public async Task AColonyUnderAttackCanStillTakeBackAndClose()
    {
        // Only what would leave the colony waits; a side that shrinks brings things home, and escrow is off the map already.
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await bob.C.Trades.OpenAsync(OpenTrade(nova.Id, Items(("Steel", 5)), silver: 10), bob.Auth);
        await SeedRaid(fixture, nova.Id, bob.Id);

        var empty = await bob.C.Trades.OpenAsync(OpenTrade(nova.Id, note: "what will you give?"), bob.Auth);
        var smaller = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 4, note: "less"), bob.Auth);
        Assert.Equal(96, await Balance(bob));
        var back = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 4, returnGoods: true), bob.Auth);
        Assert.Equal(ParcelKind.ParcelTradeReturn, Assert.Single((await bob.C.Economy.Mailbox.ListAsync(new Empty(), bob.Auth)).Parcels).Kind);
        await Complete(bob, nova, back);
        Assert.Equal(TradeStatus.TradeCompleted, (await nova.C.Trades.ListAsync(new Empty(), nova.Auth)).Trades.Single(t => t.Id == smaller.Id).Status);
        await bob.C.Trades.CancelAsync(new TradeRef { TradeId = empty.Id }, bob.Auth);
    }

    [Fact]
    public async Task ADebtorMovesNothingIntoATrade()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await bob.C.Trades.OpenAsync(OpenTrade(nova.Id, silver: 10), bob.Auth);
        await SetBalance(fixture, bob.Id, -5);

        Assert.Equal(ErrorCode.InDebt, await Refused(() => bob.C.Trades.OpenAsync(OpenTrade(nova.Id, Items(("Steel", 5))), bob.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.InDebt, await Refused(() => bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Steel", 5)), silver: 10), bob.Auth).ResponseAsync));

        await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 0), bob.Auth); // the silver comes home and pays the debt
        Assert.Equal(5, await Balance(bob));
    }

    [Fact]
    public async Task AReplayOfAnAppliedCallIsNotRefused()
    {
        // The goods left the client's game with the first call; a refusal now would put them back a second time.
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var open = OpenTrade(nova.Id, Items(("Steel", 5)), requestId: "open");
        var trade = await bob.C.Trades.OpenAsync(open, bob.Auth);
        var side = TradeSide(trade.Id, Items(("Steel", 5)), requestId: "side");
        await bob.C.Trades.SetSideAsync(side, bob.Auth);
        await SeedRaid(fixture, nova.Id, bob.Id);
        Forbid(fixture, "Steel");

        Assert.Equal(trade.Id, (await bob.C.Trades.OpenAsync(open, bob.Auth)).Id);
        Assert.Equal(trade.Id, (await bob.C.Trades.SetSideAsync(side, bob.Auth)).Id);
    }

    [Fact]
    public async Task WhatIsInATradeCountsAsItsOwnersWealth()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");

        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Worth(10, 1), PawnWorth(200), silver: 30), nova.Auth);

        Assert.Equal(70 + 30 + 10 + 200, await WealthOf(fixture, nova));
        Assert.Equal(100, await WealthOf(fixture, bob));

        await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Worth(5, 2)), bob.Auth);
        Assert.Equal(100 + 10, await WealthOf(fixture, bob));

        // Cancelled, the goods come back as a return parcel: the owner's own values, still theirs.
        await nova.C.Trades.CancelAsync(new TradeRef { TradeId = trade.Id }, nova.Auth);
        Assert.Equal(100 + 10 + 200, await WealthOf(fixture, nova));
        Assert.Equal(100 + 10, await WealthOf(fixture, bob));
    }

    [Fact]
    public async Task ATradeParcelCountsOnlyItsSilverForTheRecipient()
    {
        // The values are the partner's: an inflated one must not strip the recipient of the poor-colony protection.
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Worth(1000, 1000), silver: 30), nova.Auth);

        await Complete(nova, bob, trade);

        Assert.Equal(100 + 30, await WealthOf(fixture, bob));
        Assert.Equal(70, await WealthOf(fixture, nova));
    }

    [Fact]
    public async Task ATradeParcelPaysTheStorageFeeWhenDelivered()
    {
        using var fixture = Server();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, pawns: PawnWorth(2000)), nova.Auth);
        await Complete(nova, bob, trade);
        var parcel = Assert.Single((await bob.C.Economy.Mailbox.ListAsync(new Empty(), bob.Auth)).Parcels);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.Where(p => p.Id == parcel.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTime.UtcNow.AddDays(-2)));

        await bob.C.Economy.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 300 }, bob.Auth);

        Assert.Equal(80, await Balance(bob)); // 0.5% of 2000 for 2 days
    }
}
