using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Procurement;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Procurement;

public class ProcurementTests
{
    internal static ProcurementCreate Create(ProcurementKind kind = ProcurementKind.BuyOrder, int quantity = 10, long reward = 100, long supplier = 0) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"), Kind = kind, DefName = "Steel", Quantity = quantity,
        Reward = reward, SupplierPlayerId = supplier, LifetimeHours = 24,
    };
    internal static ProcurementSupply Supply(long id, int count, string def = "Steel") => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"), ProcurementId = id, Items = Items((def, count)), GameTick = 100,
    };
    internal static Protocol.V1.Procurement.ProcurementClient Api(ServerFixture f) => new(f.CreateChannel());
    internal static async Task<long> Balance(ServerFixture f, long id)
    {
        using var s = f.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<WalletService>().Balance(id);
    }
    internal static async Task<ProcurementEntry> Post(ServerFixture f, LoginReply buyer, ProcurementCreate r)
    {
        await Fund(f, buyer.Player.PlayerId, r.Kind == ProcurementKind.BuyOrder ? r.Reward * r.Quantity : r.Reward);
        return await Api(f).CreateAsync(r, Bearer(buyer.SessionToken));
    }

    [Fact]
    public async Task PartialOrdersPaySeveralSellersAndCancelReturnsOnlyUnspentEscrow()
    {
        using var f = EconomyServer();
        var buyer = await f.RegisterAndLogin("buyer"); var a = await f.RegisterAndLogin("seller-a"); var b = await f.RegisterAndLogin("seller-b");
        var api = Api(f); var order = await Post(f, buyer, Create());
        var r = Supply(order.Id, 2);
        await api.SupplyAsync(r, Bearer(a.SessionToken)); await api.SupplyAsync(r, Bearer(a.SessionToken));
        await api.SupplyAsync(Supply(order.Id, 3), Bearer(b.SessionToken));
        var cancel = new ProcurementCancel { ClientRequestId = Guid.NewGuid().ToString("N"), ProcurementId = order.Id };
        await api.CancelAsync(cancel, Bearer(buyer.SessionToken)); await api.CancelAsync(cancel, Bearer(buyer.SessionToken));
        Assert.Equal(500, await Balance(f, buyer.Player.PlayerId)); Assert.Equal(200, await Balance(f, a.Player.PlayerId)); Assert.Equal(300, await Balance(f, b.Player.PlayerId));
        var mail = await Clients(f, buyer.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(buyer.SessionToken));
        Assert.Equal(5, mail.Parcels.Sum(p => p.Items.Items.Sum(i => i.Count)));
    }

    [Fact]
    public async Task ContractRequiresTheAddressedSupplierAndExactGoodsBeforeDeadline()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var a = await f.RegisterAndLogin("seller"); var b = await f.RegisterAndLogin("other");
        var api = Api(f); var order = await Post(f, buyer, Create(ProcurementKind.SupplyContract, supplier: a.Player.PlayerId));
        await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(Supply(order.Id, 10), Bearer(b.SessionToken)).ResponseAsync);
        await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(Supply(order.Id, 9), Bearer(a.SessionToken)).ResponseAsync);
        await Assert.ThrowsAsync<RpcException>(() => api.SupplyAsync(Supply(order.Id, 10, "WoodLog"), Bearer(a.SessionToken)).ResponseAsync);
        var done = await api.SupplyAsync(Supply(order.Id, 10), Bearer(a.SessionToken));
        Assert.Equal(ProcurementStatus.ProcurementCompleted, done.Status); Assert.Equal(100, await Balance(f, a.Player.PlayerId));
    }

    [Fact]
    public async Task CommunityHoldsContributionsAndSplitsRemainderByPlayerId()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var a = await f.RegisterAndLogin("seller-a"); var b = await f.RegisterAndLogin("seller-b");
        var api = Api(f); var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal, 3, 10));
        await api.SupplyAsync(Supply(order.Id, 1), Bearer(b.SessionToken));
        Assert.Equal(0, await Balance(f, b.Player.PlayerId));
        Assert.Empty((await Clients(f, buyer.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(buyer.SessionToken))).Parcels);
        await api.SupplyAsync(Supply(order.Id, 2), Bearer(a.SessionToken));
        Assert.Equal(7, await Balance(f, a.Player.PlayerId)); Assert.Equal(3, await Balance(f, b.Player.PlayerId));
        Assert.Equal(3, (await Clients(f, buyer.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(buyer.SessionToken))).Parcels.Sum(p => p.Items.Items.Sum(i => i.Count)));
    }

    [Fact]
    public async Task ExpiredCommunityReturnsGoodsAndWholeFundExactlyOnce()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var a = await f.RegisterAndLogin("seller");
        var api = Api(f); var order = await Post(f, buyer, Create(ProcurementKind.CommunityGoal));
        await api.SupplyAsync(Supply(order.Id, 3), Bearer(a.SessionToken));
        using var s = f.Services.CreateScope(); var service = s.ServiceProvider.GetRequiredService<ProcurementService>();
        Assert.Equal(1, await service.ExpireDue(DateTime.UtcNow.AddDays(2))); Assert.Equal(0, await service.ExpireDue(DateTime.UtcNow.AddDays(2)));
        Assert.Equal(100, await Balance(f, buyer.Player.PlayerId)); Assert.Equal(0, await Balance(f, a.Player.PlayerId));
        Assert.Equal(3, Assert.Single((await Clients(f, a.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(a.SessionToken))).Parcels).Items.Items[0].Count);
    }

    [Fact]
    public async Task RequestIdIsScopedToActorAndBoundToOriginalIntent()
    {
        using var f = EconomyServer(); var buyer = await f.RegisterAndLogin("buyer"); var other = await f.RegisterAndLogin("other");
        var r = Create(); var order = await Post(f, buyer, r); var api = Api(f);
        Assert.Equal(order.Id, (await api.CreateAsync(r, Bearer(buyer.SessionToken))).Id);
        var changed = r.Clone(); changed.Quantity++;
        var ex = await Assert.ThrowsAsync<RpcException>(() => api.CreateAsync(changed, Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.ProcurementIntent, ex.ErrorDetail()?.Code);
        var second = await Post(f, other, r); Assert.NotEqual(order.Id, second.Id);
    }
}
