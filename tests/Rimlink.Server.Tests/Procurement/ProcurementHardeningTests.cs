extern alias tools;
using System.Net;
using System.Net.Http.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Procurement;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Procurement.ProcurementTests;
using Raid = Rimlink.Server.Storage.Entities.Raid;

namespace Rimlink.Server.Tests.Procurement;

public class ProcurementHardeningTests
{
    private static async Task Db(ServerFixture f, Func<RimlinkDbContext, Task> action)
    {
        using var s = f.Services.CreateScope(); await action(s.ServiceProvider.GetRequiredService<RimlinkDbContext>());
    }
    private static async Task Freeze(ServerFixture f, long p)
    {
        using var s = f.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<PlayerFreeze>().Set(p, true, "test");
    }

    [Theory]
    [InlineData("debt", ErrorCode.InDebt)]
    [InlineData("freeze", ErrorCode.PlayerFrozen)]
    [InlineData("threat", ErrorCode.UnderThreat)]
    [InlineData("global", ErrorCode.EconomyFrozen)]
    [InlineData("forbidden", ErrorCode.DefForbidden)]
    public async Task OutgoingGuardsRefuseWithoutMovingMoneyOrGoods(string mode, ErrorCode expected)
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var api = Api(f); var order = await Post(f, buyer, Create());
        if (mode == "debt") using (var s = f.Services.CreateScope()) await s.ServiceProvider.GetRequiredService<WalletService>().Charge(seller.Player.PlayerId, 1, "storage", 0);
        if (mode == "freeze") await Freeze(f, seller.Player.PlayerId);
        if (mode == "global") f.Services.GetRequiredService<FreezeState>().Frozen = true;
        if (mode == "forbidden") f.Services.GetRequiredService<ForbiddenDefs>().Replace(["Steel"]);
        if (mode == "threat") await Db(f, async db => { db.Raids.Add(new Raid { AttackerPlayerId = buyer.Player.PlayerId, TargetPlayerId = seller.Player.PlayerId, State = RaidStateDb.Queued, Pawns = new PawnList().ToByteArray(), CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) }); await db.SaveChangesAsync(); });
        var ex = await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(Supply(order.Id, 1), Bearer(seller.SessionToken)).ResponseAsync);
        Assert.Equal(expected, ex.ErrorDetail()?.Code);
        await Db(f, async db => { Assert.Equal(0, await db.Set<ProcurementDelivery>().CountAsync()); Assert.Equal(1000, (await db.Set<ProcurementOrder>().SingleAsync()).Escrow); });
    }

    [Fact]
    public async Task ReplaySucceedsBeforeChangedGatesAndHiddenFrozenBuyerCannotTrade()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var r = Create(); var order = await Post(f, buyer, r); var api = Api(f); var supply = Supply(order.Id, 1);
        await api.SupplyAsync(supply, Bearer(seller.SessionToken));
        await Freeze(f, buyer.Player.PlayerId); await Freeze(f, seller.Player.PlayerId);
        f.Services.GetRequiredService<FreezeState>().Frozen = true;
        await api.CreateAsync(r, Bearer(buyer.SessionToken)); await api.SupplyAsync(supply, Bearer(seller.SessionToken));
        Assert.Equal(100, await Balance(f, seller.Player.PlayerId));
        Assert.Empty((await api.ListAsync(new ProcurementQuery(), Bearer(seller.SessionToken))).Entries);
        await api.CancelAsync(new ProcurementCancel { ProcurementId = order.Id, ClientRequestId = "return" }, Bearer(buyer.SessionToken));
        Assert.Equal(900, await Balance(f, buyer.Player.PlayerId));
    }

    [Fact]
    public async Task TwoSellersRaceForLastQuotaOnlyOnePaysAndOneParcelExists()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var a = await f.RegisterAndLogin("seller-a"); var b = await f.RegisterAndLogin("seller-b");
        var order = await Post(f, buyer, Create(quantity: 1)); var api = Api(f);
        async Task<bool> Try(LoginReply seller) { try { await api.SupplyAsync(Supply(order.Id, 1), Bearer(seller.SessionToken)); return true; } catch (RpcException ex) { Assert.Equal(ErrorCode.ProcurementGone, ex.ErrorDetail()?.Code); return false; } }
        var results = await Task.WhenAll(Try(a), Try(b)); Assert.Single(results, x => x);
        Assert.Equal(100, await Balance(f, a.Player.PlayerId) + await Balance(f, b.Player.PlayerId));
        await Db(f, async db => { Assert.Equal(1, await db.Set<ProcurementDelivery>().CountAsync()); Assert.Equal(1, await db.Parcels.CountAsync()); });
    }

    [Fact]
    public async Task CancelAndSupplyRaceConserveMoneyAndGoods()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal, 1, 100)); var api = Api(f);
        async Task SupplyOrRefuse() { try { await api.SupplyAsync(Supply(order.Id, 1), Bearer(seller.SessionToken)); } catch (RpcException ex) { Assert.Equal(ErrorCode.ProcurementGone, ex.ErrorDetail()?.Code); } }
        await Task.WhenAll(SupplyOrRefuse(), api.CancelAsync(new ProcurementCancel { ClientRequestId = "cancel-race", ProcurementId = order.Id }, Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(100, await Balance(f, buyer.Player.PlayerId) + await Balance(f, seller.Player.PlayerId));
        await Db(f, async db => {
            Assert.Equal(0, (await db.Set<ProcurementOrder>().SingleAsync()).Escrow);
            int accepted = await db.Set<ProcurementDelivery>().SumAsync(d => d.Quantity);
            Assert.Equal(accepted, (await db.Parcels.ToListAsync()).Sum(p => ItemList.Parser.ParseFrom(p.Items).Items.Sum(i => i.Count)));
        });
    }

    [Fact]
    public async Task ClosedReplayAndExpirySurviveRestart()
    {
        var f = EconomyServer().KeepDataDir(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal)); var request = Supply(order.Id, 2);
        await Api(f).SupplyAsync(request, Bearer(seller.SessionToken));
        string data = f.DataDir; f.Dispose();
        using var restarted = EconomyServer().WithoutWorld().WithDataDir(data);
        var again = await restarted.RegisterAndLogin("seller");
        using (var s = restarted.Services.CreateScope()) await s.ServiceProvider.GetRequiredService<ProcurementService>().ExpireDue(DateTime.UtcNow.AddDays(2));
        var replay = await Api(restarted).SupplyAsync(request, Bearer(again.SessionToken));
        Assert.Equal(ProcurementStatus.ProcurementExpired, replay.Status); Assert.Equal(100, await Balance(restarted, buyer.Player.PlayerId));
        await Db(restarted, async db => Assert.Equal(1, await db.Parcels.CountAsync()));
    }

    [Fact]
    public async Task ExpiryDefersOverflowingRefundWithoutBlockingOtherBuyersAndRetriesExactlyOnce()
    {
        using var f = EconomyServer();
        var firstBuyer = await f.RegisterAndLogin("first-buyer");
        var secondBuyer = await f.RegisterAndLogin("second-buyer");
        var first = await Post(f, firstBuyer, Create(quantity: 1));
        var second = await Post(f, secondBuyer, Create(quantity: 1));
        await Fund(f, firstBuyer.Player.PlayerId, long.MaxValue);
        var deadline = DateTime.UtcNow.AddDays(2);

        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ProcurementService>();
        Assert.Equal(1, await service.ExpireDue(deadline));
        Assert.Equal(0, await service.ExpireDue(deadline));
        Assert.Equal(long.MaxValue, await Balance(f, firstBuyer.Player.PlayerId));
        Assert.Equal(100, await Balance(f, secondBuyer.Player.PlayerId));
        await Db(f, async db =>
        {
            var held = await db.Set<ProcurementOrder>().SingleAsync(o => o.Id == first.Id);
            Assert.Equal(ProcurementStatus.ProcurementOpen, held.Status);
            Assert.Equal(100, held.Escrow);
            var returned = await db.Set<ProcurementOrder>().SingleAsync(o => o.Id == second.Id);
            Assert.Equal(ProcurementStatus.ProcurementExpired, returned.Status);
            Assert.Equal(0, returned.Escrow);
            var refund = Assert.Single(await db.WalletEntries.Where(e => e.Kind == "procurement-refund").ToListAsync());
            Assert.Equal(secondBuyer.Player.PlayerId, refund.PlayerId);
            Assert.Equal(second.Id, refund.RefId);
            Assert.Equal(100, refund.Delta);
        });

        using (var debitScope = f.Services.CreateScope())
            await debitScope.ServiceProvider.GetRequiredService<WalletService>().Apply(firstBuyer.Player.PlayerId, -100, "test-debit", 0);
        Assert.Equal(1, await service.ExpireDue(deadline));
        Assert.Equal(0, await service.ExpireDue(deadline));
        Assert.Equal(long.MaxValue, await Balance(f, firstBuyer.Player.PlayerId));
        Assert.Equal(100, await Balance(f, secondBuyer.Player.PlayerId));
        await Db(f, async db =>
        {
            Assert.All(await db.Set<ProcurementOrder>().ToListAsync(), order =>
            {
                Assert.Equal(ProcurementStatus.ProcurementExpired, order.Status);
                Assert.Equal(0, order.Escrow);
            });
            var refunds = await db.WalletEntries.Where(e => e.Kind == "procurement-refund").ToListAsync();
            Assert.Equal(2, refunds.Count);
            Assert.Equal(firstBuyer.Player.PlayerId, Assert.Single(refunds, e => e.RefId == first.Id).PlayerId);
            Assert.Equal(secondBuyer.Player.PlayerId, Assert.Single(refunds, e => e.RefId == second.Id).PlayerId);
            Assert.All(refunds, refund => Assert.Equal(100, refund.Delta));
        });
    }

    [Fact]
    public async Task WalletOverflowRollsBackAcceptedGoodsAndQuota()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create(quantity: 1)); await Fund(f, seller.Player.PlayerId, long.MaxValue);
        var ex = await Assert.ThrowsAsync<RpcException>(() => Api(f).SupplyAsync(Supply(order.Id, 1), Bearer(seller.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.ProcurementInvalid, ex.ErrorDetail()?.Code);
        await Db(f, async db => { Assert.Equal(0, await db.Set<ProcurementDelivery>().CountAsync()); Assert.Empty(await db.Parcels.ToListAsync()); Assert.Equal(0, (await db.Set<ProcurementOrder>().SingleAsync()).Supplied); });
        Assert.Equal(long.MaxValue, await Balance(f, seller.Player.PlayerId));
    }

    [Fact]
    public async Task RewardAndItemLimitsAreCheckedBeforeReservingOrAccepting()
    {
        using var f = EconomyServer().WithSetting("Economy:MaxParcelBytes", "200"); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        await Fund(f, buyer.Player.PlayerId, 1000); var api = Api(f);
        var tooMuch = Create(quantity: int.MaxValue, reward: long.MaxValue);
        await Assert.ThrowsAsync<RpcException>(() => api.CreateAsync(tooMuch, Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(1000, await Balance(f, buyer.Player.PlayerId));
        var order = await api.CreateAsync(Create(quantity: 1), Bearer(buyer.SessionToken));
        var oversized = Supply(order.Id, 1); oversized.Items.Items[0].ScribeXmlZstd = ByteString.CopyFrom(new byte[250]);
        await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(oversized, Bearer(seller.SessionToken)).ResponseAsync);
        var excessive = Supply(order.Id, 2);
        await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(excessive, Bearer(seller.SessionToken)).ResponseAsync);
        await Db(f, async db => Assert.Equal(0, await db.Set<ProcurementDelivery>().CountAsync()));
    }

    [Fact]
    public async Task HeldFundAndGoodsCountOnceInServerWealthAndFeesUseExistingRules()
    {
        using var f = EconomyServer().WithSetting("Economy:MarketFeePercent", "10").WithSetting("Economy:StorageFeePercentPerDay", "10").WithSetting("Economy:StorageFreeDays", "0");
        var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal, 2, 100)); var request = Supply(order.Id, 1);
        request.Items.Items[0].MarketValueX100 = 10000; request.Items.Items[0].Perishable = true;
        await Api(f).SupplyAsync(request, Bearer(seller.SessionToken));
        using (var s = f.Services.CreateScope()) { var wealth = s.ServiceProvider.GetRequiredService<ServerWealth>(); Assert.Equal(100, await wealth.Of(buyer.Player.PlayerId)); Assert.Equal(100, await wealth.Of(seller.Player.PlayerId)); }
        await Db(f, db => db.Set<ProcurementDelivery>().ExecuteUpdateAsync(s => s.SetProperty(d => d.CreatedAt, DateTime.UtcNow.AddDays(-1))));
        await Api(f).SupplyAsync(Supply(order.Id, 1), Bearer(seller.SessionToken));
        Assert.Equal(80, await Balance(f, seller.Player.PlayerId)); Assert.Equal(20, await Balance(f, 0));
        using (var s = f.Services.CreateScope()) Assert.Equal(80, await s.ServiceProvider.GetRequiredService<ServerWealth>().Of(seller.Player.PlayerId));
        await Db(f, async db => Assert.Equal(100, await db.WalletEntries.SumAsync(e => e.Delta)));
    }

    [Fact]
    public async Task AddressedContractsArePrivateAndForeignCancellationIsDenied()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller"); var other = await f.RegisterAndLogin("other");
        var order = await Post(f, buyer, Create(ProcurementKind.SupplyContract, supplier: seller.Player.PlayerId)); var api = Api(f);
        Assert.Empty((await api.ListAsync(new ProcurementQuery(), Bearer(other.SessionToken))).Entries);
        Assert.Single((await api.ListAsync(new ProcurementQuery(), Bearer(seller.SessionToken))).Entries);
        await Assert.ThrowsAsync<RpcException>(() => api.CancelAsync(new ProcurementCancel { ProcurementId = order.Id, ClientRequestId = "foreign" }, Bearer(other.SessionToken)).ResponseAsync);
        Assert.Equal(0, await Balance(f, buyer.Player.PlayerId));
    }

    [Fact]
    public async Task ExpiredContractRefusesSupplyEvenBeforeBackgroundSweep()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create(ProcurementKind.SupplyContract, supplier: seller.Player.PlayerId));
        await Db(f, db => db.Set<ProcurementOrder>().ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));
        var ex = await Assert.ThrowsAsync<RpcException>(() => Api(f).SupplyAsync(Supply(order.Id, 10), Bearer(seller.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.ProcurementGone, ex.ErrorDetail()?.Code);
        using var scope = f.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<ProcurementService>().ExpireDue(DateTime.UtcNow);
        Assert.Equal(100, await Balance(f, buyer.Player.PlayerId));
    }

    [Fact]
    public async Task ResetAndArchiveReturnCommunityEscrow()
    {
        using var f = HygieneFixtures.HygieneServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await HygieneFixtures.Settled(f, "seller", 100);
        var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal)); await Api(f).SupplyAsync(Supply(order.Id, 2), Bearer(seller.Token));
        f.Services.GetRequiredService<SessionRegistry>().End(seller.Token);
        using (var scope = f.Services.CreateScope()) Assert.IsType<ArchiveOutcome.Archived>(await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(seller.PlayerId, "test", Actor.Api));
        Assert.Equal(100, await Balance(f, buyer.Player.PlayerId));
        var second = await Post(f, buyer, Create());
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, buyer.Player.PlayerId);
        Assert.Equal(1100, await Balance(f, buyer.Player.PlayerId));
        await Db(f, async db => Assert.Equal(0, await db.Set<ProcurementOrder>().CountAsync(o => o.Status == ProcurementStatus.ProcurementOpen)));
    }

    [Fact]
    public async Task RestAndCliListAndCancelAreScopedAndAudited()
    {
        using var f = EconomyServer().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithToken(0, "reader", new string('r', 24), "read");
        var buyer = await f.RegisterAndLogin("buyer"); var order = await Post(f, buyer, Create());
        using var reader = f.Rest(new string('r', 24)); Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/procurement")).StatusCode);
        using var http = f.Rest();
        Assert.Contains("Steel", await http.GetStringAsync("/api/admin/procurement"));
        using var cliHttp = f.Rest(); cliHttp.BaseAddress = new Uri(cliHttp.BaseAddress!, "/api/admin/");
        using var rest = new tools::Rimlink.Tools.Commands.RestClient(cliHttp);
        Assert.Contains("Steel", await tools::Rimlink.Tools.Commands.AdminCommand.Run(rest, "procurement", ["list"]));
        await tools::Rimlink.Tools.Commands.AdminCommand.Run(rest, "procurement", ["cancel", order.Id.ToString(), "test"]);
        await http.PostAsJsonAsync($"/api/admin/procurement/{order.Id}/cancel", new { reason = "again" });
        Assert.Equal(1000, await Balance(f, buyer.Player.PlayerId));
        await Db(f, async db => Assert.Equal(1, await db.AdminActions.CountAsync(a => a.Action == "procurement-cancel")));
    }

    [Fact]
    public async Task AutomaticArchiveCannotTreatReservedSilverAsHiddenWealth()
    {
        using var f = HygieneFixtures.HygieneServer().WithSetting("Hygiene:ArchiveAfterDays", "1").WithSetting("Hygiene:ArchiveMaxWealth", "100").WithSetting("Hygiene:ArchiveMaxColonyDays", "0");
        var buyer = await HygieneFixtures.Settled(f, "buyer", 100, wealth: 1);
        await Post(f, buyer.Login, Create());
        f.Services.GetRequiredService<SessionRegistry>().End(buyer.Token);
        await HygieneFixtures.SetActivity(f, buyer.PlayerId, DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-10));
        using var scope = f.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ColonyHygiene>().SweepDue(DateTime.UtcNow));
    }

    [Fact]
    public async Task AdminReturnsHeldGoodsWithoutStoragePenalty()
    {
        using var f = EconomyServer().WithSetting("Economy:StorageFeePercentPerDay", "100").WithSetting("Economy:StorageFreeDays", "0");
        var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal)); var r = Supply(order.Id, 1);
        r.Items.Items[0].Perishable = true; r.Items.Items[0].MarketValueX100 = 10000;
        await Api(f).SupplyAsync(r, Bearer(seller.SessionToken));
        await Db(f, db => db.Set<ProcurementDelivery>().ExecuteUpdateAsync(s => s.SetProperty(d => d.CreatedAt, DateTime.UtcNow.AddDays(-5))));
        using var scope = f.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<ProcurementService>().CancelAdmin(order.Id);
        Assert.Equal(0, await Balance(f, seller.Player.PlayerId));
        Assert.Equal(100, await Balance(f, buyer.Player.PlayerId));
    }

    [Fact]
    public async Task LastContributionCannotMovePreviouslyFrozenSuppliersGoods()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var a = await f.RegisterAndLogin("seller-a"); var b = await f.RegisterAndLogin("seller-b");
        var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal, 2)); var api = Api(f);
        await api.SupplyAsync(Supply(order.Id, 1), Bearer(a.SessionToken));
        await Freeze(f, a.Player.PlayerId);
        var ex = await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(Supply(order.Id, 1), Bearer(b.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.PlayerFrozen, ex.ErrorDetail()?.Code);
        await Db(f, async db => { Assert.Equal(1, await db.Set<ProcurementDelivery>().CountAsync()); Assert.Equal(100, (await db.Set<ProcurementOrder>().SingleAsync()).Escrow); Assert.Equal(0, await db.Parcels.CountAsync()); });
    }

    [Fact]
    public async Task DeclaredShipmentValueIsBoundedBeforeItCanOverflowImportAccounting()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create()); var request = Supply(order.Id, 2);
        request.Items.Items[0].MarketValueX100 = long.MaxValue;
        var ex = await Assert.ThrowsAsync<RpcException>(() => Api(f).SupplyAsync(request, Bearer(seller.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.ProcurementGoods, ex.ErrorDetail()?.Code);
        await Db(f, async db => Assert.Equal(0, await db.Set<ProcurementDelivery>().CountAsync()));
    }

    [Fact]
    public async Task ResetOfAnAlreadyPaidSellerDoesNotCancelSomeoneElsesRemainingBuyOrder()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var seller = await f.RegisterAndLogin("seller");
        var order = await Post(f, buyer, Create()); var api = Api(f);
        await api.SupplyAsync(Supply(order.Id, 1), Bearer(seller.SessionToken));
        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, seller.Player.PlayerId);
        var open = Assert.Single((await api.ListAsync(new ProcurementQuery(), Bearer(buyer.SessionToken))).Entries);
        Assert.Equal(ProcurementStatus.ProcurementOpen, open.Status); Assert.Equal(900, open.Escrow);
    }
}
