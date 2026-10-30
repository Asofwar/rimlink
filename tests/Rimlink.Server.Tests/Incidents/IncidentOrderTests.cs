using Microsoft.EntityFrameworkCore;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.IncidentFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Incidents;

/// <summary>Ordering paid incidents (paid incidents spec §2).</summary>
public class IncidentOrderTests
{
    private sealed record Trio(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, LoginReply Cara, Protocol.V1.Incidents.IncidentsClient Api)
    {
        public Metadata N => Bearer(Nova.SessionToken);
        public Metadata B => Bearer(Bob.SessionToken);
        public Metadata C => Bearer(Cara.SessionToken);
        public long BobId => Bob.Player.PlayerId;
    }

    /// <summary>Nova and Cara (10000 silver each) order against Bob (raidable, wealth 100000).</summary>
    private static async Task<Trio> Players(ServerFixture? fixture = null)
    {
        fixture ??= IncidentServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var cara = await fixture.RegisterAndLogin("cara", playerName: "Cara");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        await Fund(fixture, nova.Player.PlayerId, 10000);
        await Fund(fixture, cara.Player.PlayerId, 10000);
        return new Trio(fixture, nova, bob, cara, IncidentsOf(fixture));
    }

    private static async Task<long> BalanceOf(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(playerId);
    }

    private static async Task AssertRefused(Func<Task> call, ErrorCode code)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, CodeOf(ex));
    }

    [Fact]
    public async Task CatalogListsTheDefaults()
    {
        var p = await Players();
        using var fixture = p.Fixture;

        var catalog = await p.Api.CatalogAsync(new Empty(), p.N);

        Assert.Equal(13, catalog.Entries.Count);
        Assert.True(catalog.Entries.Single(e => e.Id == "pirates").PodsAllowed);
        Assert.False(catalog.Entries.Single(e => e.Id == "tribals").PodsAllowed);
        Assert.Equal(IncidentKind.IncidentWeather, catalog.Entries.Single(e => e.Id == "toxic-fallout").Kind);
    }

    [Fact]
    public async Task QuoteMatchesThePrice()
    {
        var p = await Players();
        using var fixture = p.Fixture;

        var two = await p.Api.QuoteAsync(new QuoteRequest { TargetPlayerId = p.BobId, EntryId = "pirates", Level = 2 }, p.N);
        var three = await p.Api.QuoteAsync(new QuoteRequest { TargetPlayerId = p.BobId, EntryId = "pirates", Level = 3 }, p.N);

        Assert.Equal((100, false), (two.Price, two.Warned));
        Assert.True(three.Warned);
    }

    [Fact]
    public async Task OrderChargesTheBuyerAndQueuesForTheTarget()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        using var bobStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), p.B);

        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);

        Assert.Equal(IncidentState.IncidentQueued, order.State);
        Assert.Equal(100, order.Price);
        Assert.Equal(9900, await BalanceOf(fixture, p.Nova.Player.PlayerId));
        Assert.Equal(100, await BalanceOf(fixture, WalletService.BankPlayerId));
        var pending = Assert.Single((await p.Api.PendingAsync(new Empty(), p.B)).Incidents);
        Assert.Equal((order.Id, "RaidEnemy", "Pirate", "Nova"), (pending.Id, pending.IncidentDef, pending.FactionDef, pending.Buyer.Name));
        Assert.Equal(order.Id, Assert.Single((await p.Api.MineAsync(new Empty(), p.N)).Incidents).Id);
        Assert.Equal(order.Id, await ReadEvent(bobStream, ServerEvent.BodyOneofCase.IncidentQueued, e => e.IncidentQueued.Incident.Id));
    }

    [Fact]
    public async Task AnotherPlayersRequestIdIsNotAReplay()
    {
        // The id comes from the client: a collision must not hand one player another's order.
        var p = await Players();
        using var fixture = p.Fixture;
        var novas = await p.Api.OrderAsync(Order(p.BobId, entry: "trader", requestId: "shared"), p.N);

        var caras = await p.Api.OrderAsync(Order(p.BobId, entry: "resource-pod", requestId: "shared"), p.C);

        Assert.NotEqual(novas.Id, caras.Id);
        Assert.Equal(("Cara", "resource-pod"), (caras.Buyer.Name, caras.EntryId));
        Assert.Equal(10000 - caras.Price, await BalanceOf(fixture, p.Cara.Player.PlayerId));
    }

    [Fact]
    public async Task ReplayReturnsTheOriginalWithoutCharging()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        var request = Order(p.BobId, entry: "trader", requestId: "o-1");
        var order = await p.Api.OrderAsync(request, p.N);
        await p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.C);
        await SetBalance(fixture, p.Nova.Player.PlayerId, 0);

        var again = await p.Api.OrderAsync(request, p.N);

        Assert.Equal(order.Id, again.Id);
        Assert.Equal(0, await BalanceOf(fixture, p.Nova.Player.PlayerId));
    }

    [Fact]
    public async Task PeacefulTargetOrBuyerRefusesHarmfulEvents()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await SetPeaceful(fixture, p.BobId);
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId), p.N).ResponseAsync, ErrorCode.PeacefulMode);
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, entry: "toxic-fallout"), p.N).ResponseAsync, ErrorCode.PeacefulMode);

        var q = await Players(IncidentServer());
        using var fixture2 = q.Fixture;
        await SetPeaceful(fixture2, q.Nova.Player.PlayerId);
        await AssertRefused(() => q.Api.OrderAsync(Order(q.BobId), q.N).ResponseAsync, ErrorCode.PeacefulMode);
    }

    [Fact]
    public async Task NewAndPoorTargetsAreProtected()
    {
        using var fixture = IncidentServer().WithSetting("Raids:NewbieProtectionDays", "3").WithSetting("Raids:MinColonyWealth", "20000");
        var p = await Players(fixture);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Rimlink.Server.Storage.RimlinkDbContext>().Players.Where(x => x.Id == p.BobId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FirstSaveAt, DateTime.UtcNow));
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId), p.N).ResponseAsync, ErrorCode.TargetProtected);

        await MakeTargetRaidable(fixture, p.BobId, wealthItems: 100, wealthTotal: 100);
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, entry: "cold-snap"), p.N).ResponseAsync, ErrorCode.TargetProtected);
        // Gifts are not refused by the protections.
        await p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.N);
    }

    [Fact]
    public async Task ARaidedTargetIsOnCooldownAndFrozenRaidsRefuse()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await RaidClientsOf(fixture, p.Cara.SessionToken).Raids.LaunchAsync(Launch(p.BobId), Bearer(p.Cara.SessionToken));
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId), p.N).ResponseAsync, ErrorCode.RaidCooldown);

        var q = await Players(IncidentServer());
        using var fixture2 = q.Fixture;
        fixture2.Services.GetRequiredService<RaidFreezeState>().Frozen = true;
        await AssertRefused(() => q.Api.OrderAsync(Order(q.BobId), q.N).ResponseAsync, ErrorCode.EconomyFrozen);
    }

    [Fact]
    public async Task KindEventsSkipRaidRules()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await SetPeaceful(fixture, p.BobId);

        var order = await p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.N);

        Assert.Equal(0, order.Level);
    }

    [Fact]
    public async Task KindCooldownPerPair()
    {
        var p = await Players(RaidServer().WithSetting("Incidents:KindCooldownHours", "1"));
        using var fixture = p.Fixture;
        await p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.N);

        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, entry: "resource-pod"), p.N).ResponseAsync, ErrorCode.RaidCooldown);
        await p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.C);
    }

    [Fact]
    public async Task QueueHoldsTwo()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.N);
        await p.Api.OrderAsync(Order(p.BobId, entry: "wanderer"), p.C);

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.Api.OrderAsync(Order(p.BobId, entry: "resource-pod"), p.N).ResponseAsync);
        Assert.Equal(ErrorCode.IncidentQueueFull, CodeOf(ex));
        Assert.Equal("Bob already has 2 events waiting", ex.Status.Detail);
    }

    [Fact]
    public async Task DebtRefusesOrders()
    {
        var p = await Players();
        using var fixture = p.Fixture;
        await SetBalance(fixture, p.Nova.Player.PlayerId, -10);

        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, entry: "trader"), p.N).ResponseAsync, ErrorCode.InDebt);
    }

    [Fact]
    public async Task BadOrdersAreRefused()
    {
        var p = await Players();
        using var fixture = p.Fixture;

        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, entry: "nope"), p.N).ResponseAsync, ErrorCode.Unspecified);
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, level: 5), p.N).ResponseAsync, ErrorCode.Unspecified);
        await AssertRefused(() => p.Api.OrderAsync(Order(p.BobId, entry: "tribals", pods: true), p.N).ResponseAsync, ErrorCode.Unspecified);
        await AssertRefused(() => p.Api.OrderAsync(Order(p.Nova.Player.PlayerId), p.N).ResponseAsync, ErrorCode.SelfTrade);
        await AssertRefused(() => p.Api.OrderAsync(Order(9999), p.N).ResponseAsync, ErrorCode.RecipientUnknown);
    }

    [Fact]
    public async Task DisabledIncidentsRefuse()
    {
        var p = await Players(IncidentServer().WithSetting("Incidents:Enabled", "false"));
        using var fixture = p.Fixture;

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.Api.OrderAsync(Order(p.BobId), p.N).ResponseAsync);
        Assert.Equal((ErrorCode.EconomyFrozen, "paid incidents are disabled on this server"), (CodeOf(ex), ex.Status.Detail));
    }

    [Fact]
    public async Task AReplayIsAnsweredWhileIncidentsAreOff()
    {
        // The order was paid before the switch: refusing its replay would tell the buyer it never happened.
        using var fixture = IncidentServer().WithSetting("Incidents:Enabled", "false");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Storage.RimlinkDbContext>();
            db.IncidentOrders.Add(new Rimlink.Server.Storage.Entities.IncidentOrder
            {
                BuyerPlayerId = nova.Player.PlayerId, TargetPlayerId = bob.Player.PlayerId, EntryId = "pirates", IncidentDef = "RaidEnemy", Level = 2, Price = 77,
                State = Rimlink.Server.Storage.Entities.IncidentStateDb.Queued, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7), ClientRequestId = "paid",
            });
            await db.SaveChangesAsync();
        }

        var replay = await IncidentsOf(fixture).OrderAsync(Order(bob.Player.PlayerId, requestId: "paid"), Bearer(nova.SessionToken));

        Assert.Equal(77, replay.Price);
    }

    [Fact]
    public async Task OrderKeepsItsDefsWhenTheCatalogChanges()
    {
        string dataDir;
        long bobId;
        string bobToken;
        using (var fixture = IncidentServer().KeepDataDir())
        {
            dataDir = fixture.DataDir;
            var p = await Players(fixture);
            await p.Api.OrderAsync(Order(p.BobId), p.N);
            bobId = p.BobId;
        }
        using var again = IncidentServer().WithDataDir(dataDir)
            .WithSetting("Incidents:Catalog:0:Id", "other").WithSetting("Incidents:Catalog:0:IncidentDef", "ColdSnap").WithSetting("Incidents:Catalog:0:Kind", "Weather");
        bobToken = (await again.RegisterAndLogin("bob", playerName: "Bob")).SessionToken;

        var pending = Assert.Single((await IncidentsOf(again).PendingAsync(new Empty(), Bearer(bobToken))).Incidents);

        Assert.Equal(("RaidEnemy", "Pirate", bobId), (pending.IncidentDef, pending.FactionDef, pending.Target.PlayerId));
    }
}
