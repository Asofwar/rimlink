using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Auctions;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;

namespace Rimlink.Server.Tests.Auctions;

public class AuctionLifecycleTests
{
    private static AuctionPostRequest Post(long price = 100, long buyout = 0, int duration = 3600) => new()
    {
        ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 5)), StartPrice = price,
        BuyoutPrice = buyout, DurationSeconds = duration, GameTick = 100,
    };
    private static AuctionBidRequest Bid(long id, long amount, bool buyout = false) => new()
    { ClientRequestId = Guid.NewGuid().ToString("N"), AuctionId = id, Amount = amount, Buyout = buyout };
    private static async Task<int> Sweep(ServerFixture f, DateTime now)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuctionService>().ExpireDue(now);
    }
    private static async Task<long> Balance(ServerFixture f, long player)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(player);
    }
    private static async Task Refused(ErrorCode code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<RpcException>(action)).ErrorDetail()?.Code);

    [Fact]
    public async Task ConcurrentRetriesOfSamePostAndBidApplyOnlyOnce()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, buyer.Player.PlayerId, 500);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post();
        var lots = await Task.WhenAll(api.PostAsync(post, Bearer(seller.SessionToken)).ResponseAsync, api.PostAsync(post, Bearer(seller.SessionToken)).ResponseAsync);
        Assert.Equal(lots[0], lots[1]);
        var bid = Bid(lots[0].Id, 100);
        var bids = await Task.WhenAll(api.BidAsync(bid, Bearer(buyer.SessionToken)).ResponseAsync, api.BidAsync(bid, Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(bids[0], bids[1]);
        Assert.Equal(400, await Balance(f, buyer.Player.PlayerId));
        Assert.Equal(1, await Db(f, db => db.Set<AuctionRow>().CountAsync()));
        Assert.Equal(2, await Db(f, db => db.Set<AuctionOperation>().CountAsync()));
    }

    [Fact]
    public async Task FrozenOrFailingAuctionsDoNotStarveLaterDeadlines()
    {
        var clock = new ManualTimeProvider();
        // The background sweep must stay earlier than the deadlines tested by the manual sweep.
        var sweepAt = clock.Now.UtcDateTime.AddHours(3);
        using var f = EconomyServer().WithServices(s => s.AddSingleton<TimeProvider>(clock));
        var frozen = await f.RegisterAndLogin("frozen"); var seller = await f.RegisterAndLogin("seller");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            for (int i = 0; i < 101; i++) db.Set<AuctionRow>().Add(new AuctionRow
            {
                SellerPlayerId = i < 100 ? frozen.Player.PlayerId : seller.Player.PlayerId, Items = Items(("Steel", 1)).ToByteArray(), StartPrice = 1,
                CreatedAt = sweepAt.AddHours(-2), ExpiresAt = sweepAt.AddHours(-1), MaxExpiresAt = sweepAt.AddMinutes(-55),
            });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PlayerFreeze>().Set(frozen.Player.PlayerId, true, "test");
        }
        Assert.Equal(1, await Sweep(f, sweepAt));
        Assert.Equal(seller.Player.PlayerId, await Db(f, db => db.Parcels.Select(p => p.ToPlayerId).SingleAsync()));
    }

    [Fact]
    public async Task RaisingOwnBidReservesOnlyDifferenceAndSellerCannotBidOrCancelAfterBid()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var s = Bearer(seller.SessionToken); var b = Bearer(buyer.SessionToken);
        await Fund(f, buyer.Player.PlayerId, 150);
        var lot = await api.PostAsync(Post(), s);
        await Refused(ErrorCode.SelfTrade, () => api.BidAsync(Bid(lot.Id, 100), s).ResponseAsync);
        await api.BidAsync(Bid(lot.Id, 100), b);
        await api.BidAsync(Bid(lot.Id, 150), b);
        Assert.Equal(0, await Balance(f, buyer.Player.PlayerId));
        await Refused(ErrorCode.AuctionHasBids, () => api.CancelAsync(new AuctionCancelRequest { ClientRequestId = "cancel", AuctionId = lot.Id }, s).ResponseAsync);
        await Refused(ErrorCode.WalletInsufficient, () => api.BidAsync(Bid(lot.Id, 151), b).ResponseAsync);
        await Refused(ErrorCode.AuctionBidTooLow, () => api.BidAsync(Bid(lot.Id, 149), b).ResponseAsync);
        Assert.Equal(150, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Bid).SingleAsync()));
    }

    [Fact]
    public async Task SnipingAddsThirtySecondsButNeverExceedsTheFiveMinuteCap()
    {
        var clock = new ManualTimeProvider();
        using var f = EconomyServer().WithServices(s => s.AddSingleton<TimeProvider>(clock));
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, buyer.Player.PlayerId, 10000);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(duration: 60), Bearer(seller.SessionToken));
        DateTime original = lot.ExpiresAt.ToDateTime();
        for (int i = 0; i < 12; i++)
        {
            clock.Now = new DateTimeOffset(lot.ExpiresAt.ToDateTime().AddSeconds(-10), TimeSpan.Zero);
            var next = await api.BidAsync(Bid(lot.Id, 100 + i), Bearer(buyer.SessionToken));
            Assert.Equal(new DateTime(Math.Min(lot.MaxExpiresAt.ToDateTime().Ticks, lot.ExpiresAt.ToDateTime().AddSeconds(30).Ticks), DateTimeKind.Utc), next.ExpiresAt.ToDateTime());
            lot = next;
        }
        Assert.Equal(original.AddMinutes(5), lot.ExpiresAt.ToDateTime());
        Assert.Equal(0, await Sweep(f, lot.ExpiresAt.ToDateTime().AddTicks(-1)));
        clock.Now = new DateTimeOffset(lot.ExpiresAt.ToDateTime(), TimeSpan.Zero);
        await Refused(ErrorCode.AuctionGone, () => api.BidAsync(Bid(lot.Id, 200), Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(1, await Sweep(f, clock.Now.UtcDateTime));
        Assert.Equal(111, await Balance(f, seller.Player.PlayerId));
    }

    [Fact]
    public async Task DeliveryFailureRollsBackSaleAndNextSweepCompletesExactlyOnceEvenWithFullMailbox()
    {
        using var f = EconomyServer().WithSetting("Economy:MaxPendingParcels", "1");
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, buyer.Player.PlayerId, 500);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        await api.BidAsync(Bid(lot.Id, 100), Bearer(buyer.SessionToken));
        await Db(f, async db =>
        {
            db.Parcels.Add(new Server.Storage.Entities.Parcel { ToPlayerId = buyer.Player.PlayerId, Items = new ItemList().ToByteArray(), Pawns = new PawnList().ToByteArray(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER auction_mail_failure BEFORE INSERT ON Parcels BEGIN SELECT RAISE(ABORT, 'test mailbox unavailable'); END;");
        });
        Assert.Equal(0, await Sweep(f, DateTime.UtcNow.AddHours(2)));
        Assert.Equal(0, await Balance(f, seller.Player.PlayerId));
        Assert.Equal(0, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Status).SingleAsync()));
        await Db(f, db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER auction_mail_failure;"));
        Assert.Equal(1, await Sweep(f, DateTime.UtcNow.AddHours(2)));
        Assert.Equal(0, await Sweep(f, DateTime.UtcNow.AddHours(2)));
        Assert.Equal(100, await Balance(f, seller.Player.PlayerId));
        Assert.Equal(400, await Balance(f, buyer.Player.PlayerId));
        Assert.Equal(2, await Db(f, db => db.Parcels.CountAsync()));
    }

    [Fact]
    public async Task SellerCreditOverflowLeavesEscrowAndCanBeRetriedAfterBalanceHasRoom()
    {
        using var f = EconomyServer().WithSetting("Economy:MarketFeePercent", "10");
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, seller.Player.PlayerId, long.MaxValue); await Fund(f, buyer.Player.PlayerId, long.MaxValue);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(long.MaxValue, long.MaxValue), Bearer(seller.SessionToken));
        var buyout = Bid(lot.Id, long.MaxValue, true);
        var error = await Assert.ThrowsAsync<RpcException>(() => api.BidAsync(buyout, Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(StatusCode.OutOfRange, error.StatusCode);
        Assert.Equal(long.MaxValue, await Balance(f, buyer.Player.PlayerId));
        Assert.Equal(0, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Bid).SingleAsync()));
        Assert.Equal(0, await Db(f, db => db.Parcels.CountAsync()));
        await Fund(f, seller.Player.PlayerId, -long.MaxValue);
        await api.BidAsync(buyout, Bearer(buyer.SessionToken));
        long fee = (long)((decimal)long.MaxValue * 10 / 100);
        Assert.Equal(long.MaxValue - fee, await Balance(f, seller.Player.PlayerId));
        Assert.Equal(fee, await Balance(f, 0));
    }

    [Fact]
    public async Task RefundOverflowRejectsNewBidWithoutLosingEitherReserve()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var one = await f.RegisterAndLogin("one"); var two = await f.RegisterAndLogin("two");
        await Fund(f, one.Player.PlayerId, 100); await Fund(f, two.Player.PlayerId, 200);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        await api.BidAsync(Bid(lot.Id, 100), Bearer(one.SessionToken));
        await Fund(f, one.Player.PlayerId, long.MaxValue);
        Assert.Equal(StatusCode.OutOfRange, (await Assert.ThrowsAsync<RpcException>(() => api.BidAsync(Bid(lot.Id, 200), Bearer(two.SessionToken)).ResponseAsync)).StatusCode);
        Assert.Equal(200, await Balance(f, two.Player.PlayerId));
        Assert.Equal(100, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Bid).SingleAsync()));
        await Fund(f, one.Player.PlayerId, -100);
        await api.BidAsync(Bid(lot.Id, 200), Bearer(two.SessionToken));
        Assert.Equal(long.MaxValue, await Balance(f, one.Player.PlayerId));
    }

    [Fact]
    public async Task ConcurrentBuyoutsAndCloseProduceOneWinnerAndConserveMoney()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var one = await f.RegisterAndLogin("one"); var two = await f.RegisterAndLogin("two");
        await Fund(f, one.Player.PlayerId, 500); await Fund(f, two.Player.PlayerId, 500);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(buyout: 300), Bearer(seller.SessionToken));
        async Task TryBuy(LoginReply buyer)
        {
            try { await api.BidAsync(Bid(lot.Id, 300, true), Bearer(buyer.SessionToken)); }
            catch (RpcException ex) { Assert.Equal(ErrorCode.AuctionGone, ex.ErrorDetail()?.Code); }
        }
        await Task.WhenAll(TryBuy(one), TryBuy(two), Sweep(f, DateTime.UtcNow.AddHours(2)));
        Assert.Equal(1, await Db(f, db => db.Parcels.CountAsync()));
        long total = await Balance(f, seller.Player.PlayerId) + await Balance(f, one.Player.PlayerId) + await Balance(f, two.Player.PlayerId);
        Assert.Equal(1000, total);
        Assert.NotEqual(0, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Status).SingleAsync()));
    }

    [Fact]
    public async Task AdminCancellationReturnsBothEscrowsAndRepeatedCancellationIsHarmless()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, buyer.Player.PlayerId, 500);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        await api.BidAsync(Bid(lot.Id, 100), Bearer(buyer.SessionToken));
        f.Services.GetRequiredService<FreezeState>().Frozen = true;
        using var scope = f.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AuctionService>();
        Assert.True(await service.AdminCancel(lot.Id, "admin"));
        Assert.False(await service.AdminCancel(lot.Id, "admin"));
        Assert.Equal(500, await Balance(f, buyer.Player.PlayerId));
        Assert.Equal(seller.Player.PlayerId, await Db(f, db => db.Parcels.Select(p => p.ToPlayerId).SingleAsync()));
    }

    [Fact]
    public async Task StorageFeeAndBothEscrowsAreCountedInWealth()
    {
        using var f = EconomyServer().WithSetting("Economy:StorageFeePercentPerDay", "10").WithSetting("Economy:StorageFreeDays", "0");
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        await Fund(f, buyer.Player.PlayerId, 500);
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post(); post.Items.Items[0].MarketValueX100 = 10000; post.Items.Items[0].Perishable = true;
        var lot = await api.PostAsync(post, Bearer(seller.SessionToken));
        await api.BidAsync(Bid(lot.Id, 100), Bearer(buyer.SessionToken));
        using (var scope = f.Services.CreateScope())
        {
            var wealth = scope.ServiceProvider.GetRequiredService<ServerWealth>();
            Assert.Equal(500, await wealth.Of(seller.Player.PlayerId));
            Assert.Equal(500, await wealth.Of(buyer.Player.PlayerId));
        }
        await Db(f, db => db.Set<AuctionRow>().ExecuteUpdateAsync(a => a.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-2))));
        await Sweep(f, DateTime.UtcNow.AddHours(2));
        Assert.Equal(0, await Balance(f, seller.Player.PlayerId)); // 100 proceeds - floor(500 * 10% * 2 days)
        Assert.Equal(100, await Balance(f, 0));
    }

    [Fact]
    public async Task EscrowAndOperationIdentitySurviveServerRestartAndOfflineExpiry()
    {
        string dir;
        long sellerId, buyerId, lotId;
        var post = Post();
        using (var f = EconomyServer().KeepDataDir())
        {
            var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
            sellerId = seller.Player.PlayerId; buyerId = buyer.Player.PlayerId;
            await Fund(f, buyerId, 500);
            var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
            lotId = (await api.PostAsync(post, Bearer(seller.SessionToken))).Id;
            await api.BidAsync(Bid(lotId, 100), Bearer(buyer.SessionToken));
            dir = f.DataDir;
        }
        using var restarted = EconomyServer().WithDataDir(dir).WithoutWorld();
        using var scope = restarted.Services.CreateScope();
        var auctions = scope.ServiceProvider.GetRequiredService<AuctionService>();
        Assert.Equal(lotId, (await auctions.Post(sellerId, post)).Id);
        Assert.Equal(1, await auctions.ExpireDue(DateTime.UtcNow.AddHours(2)));
        Assert.Equal(0, await auctions.ExpireDue(DateTime.UtcNow.AddHours(2)));
        Assert.Equal(100, await Balance(restarted, sellerId));
        Assert.Equal(400, await Balance(restarted, buyerId));
    }
}
