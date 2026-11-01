using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auctions;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Auctions;

public class AuctionTests
{
    private static AuctionPostRequest Post(long price = 100, long buyout = 0) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 5)),
        StartPrice = price, BuyoutPrice = buyout, DurationSeconds = 3600, GameTick = 100,
    };
    private static AuctionBidRequest Bid(long id, long amount, bool buyout = false) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"), AuctionId = id, Amount = amount, Buyout = buyout,
    };

    [Fact]
    public async Task OutbidRefundsImmediatelyAndBuyoutPaysSellerOnce()
    {
        using var f = EconomyServer().WithSetting("Economy:MarketFeePercent", "10");
        var seller = await f.RegisterAndLogin("seller");
        var one = await f.RegisterAndLogin("one");
        var two = await f.RegisterAndLogin("two");
        await Fund(f, one.Player.PlayerId, 1000);
        await Fund(f, two.Player.PlayerId, 1000);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post(buyout: 300);
        var lot = await api.PostAsync(post, Bearer(seller.SessionToken));
        Assert.Equal(lot, await api.PostAsync(post, Bearer(seller.SessionToken)));
        await api.BidAsync(Bid(lot.Id, 100), Bearer(one.SessionToken));
        var buy = Bid(lot.Id, 300, true);
        var result = await api.BidAsync(buy, Bearer(two.SessionToken));
        Assert.Equal(AuctionStatus.AuctionSold, result.Status);
        Assert.Equal(result, await api.BidAsync(buy, Bearer(two.SessionToken)));
        using var scope = f.Services.CreateScope();
        var wallet = scope.ServiceProvider.GetRequiredService<WalletService>();
        Assert.Equal(1000, await wallet.Balance(one.Player.PlayerId));
        Assert.Equal(700, await wallet.Balance(two.Player.PlayerId));
        Assert.Equal(270, await wallet.Balance(seller.Player.PlayerId));
        Assert.Equal(30, await wallet.Balance(0));
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        var parcel = Assert.Single(await db.Parcels.ToListAsync());
        Assert.Equal(two.Player.PlayerId, parcel.ToPlayerId);
        Assert.Equal(5, ItemList.Parser.ParseFrom(parcel.Items).Items[0].Count);
        Assert.Empty(result.Items.Items[0].ScribeXmlZstd);
    }

    [Fact]
    public async Task ReplayIsBoundToPlayerAndActualIntent()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller");
        var other = await f.RegisterAndLogin("other");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post();
        var original = await api.PostAsync(post, Bearer(seller.SessionToken));
        var wrongKind = new AuctionCancelRequest { ClientRequestId = post.ClientRequestId, AuctionId = original.Id };
        Assert.Equal(ErrorCode.AuctionIntentMismatch, (await Assert.ThrowsAsync<RpcException>(() => api.CancelAsync(wrongKind, Bearer(seller.SessionToken)).ResponseAsync)).ErrorDetail()?.Code);
        post.StartPrice++;
        var ex = await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(post, Bearer(seller.SessionToken)).ResponseAsync);
        Assert.Equal(ErrorCode.AuctionIntentMismatch, ex.ErrorDetail()?.Code);
        post.StartPrice--;
        var otherLot = await api.PostAsync(post, Bearer(other.SessionToken));
        Assert.Equal(other.Player.PlayerId, otherLot.Seller.PlayerId);
    }

    [Fact]
    public async Task ExpiryWithoutBidsAndConcurrentSweepReturnOnlyOneParcel()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        async Task<int> Sweep()
        {
            using var s = f.Services.CreateScope();
            return await s.ServiceProvider.GetRequiredService<AuctionService>().ExpireDue(DateTime.UtcNow.AddHours(2));
        }
        var results = await Task.WhenAll(Task.Run(Sweep), Task.Run(Sweep));
        Assert.Equal(1, results.Sum());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(seller.Player.PlayerId, Assert.Single(await db.Parcels.ToListAsync()).ToPlayerId);
    }
}
