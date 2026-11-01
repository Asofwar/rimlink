using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Auctions;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Application.World;
using Rimlink.Server.Storage;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.HygieneFixtures;

namespace Rimlink.Server.Tests.Auctions;

public class AuctionGuardTests
{
    private static AuctionPostRequest Post() => new()
    { ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 5)), StartPrice = 100, DurationSeconds = 3600, GameTick = 100 };
    private static AuctionBidRequest Bid(long id, long amount = 100) => new()
    { ClientRequestId = Guid.NewGuid().ToString("N"), AuctionId = id, Amount = amount };

    [Theory]
    [InlineData("seller_frozen", ErrorCode.PlayerFrozen)]
    [InlineData("buyer_frozen", ErrorCode.PlayerFrozen)]
    [InlineData("global_frozen", ErrorCode.EconomyFrozen)]
    [InlineData("buyer_threat", ErrorCode.UnderThreat)]
    [InlineData("buyer_debt", ErrorCode.InDebt)]
    [InlineData("blocked", ErrorCode.Blocked)]
    public async Task BidGatesPreserveFundsAndGoods(string kind, ErrorCode code)
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        await Fund(f, buyer.Player.PlayerId, 500);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            if (kind.EndsWith("_frozen") && kind != "global_frozen")
                await scope.ServiceProvider.GetRequiredService<PlayerFreeze>().Set(kind.StartsWith("seller") ? seller.Player.PlayerId : buyer.Player.PlayerId, true, "test");
            if (kind == "global_frozen") f.Services.GetRequiredService<FreezeState>().Frozen = true;
            if (kind.EndsWith("_threat"))
            {
                db.Raids.Add(new Server.Storage.Entities.Raid { AttackerPlayerId = kind.StartsWith("seller") ? buyer.Player.PlayerId : seller.Player.PlayerId,
                    TargetPlayerId = kind.StartsWith("seller") ? seller.Player.PlayerId : buyer.Player.PlayerId, State = RaidStateDb.Announced,
                    Controller = (int)RaidController.ControllerAi, Pawns = new PawnList().ToByteArray(), CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7) });
                await db.SaveChangesAsync();
            }
            if (kind == "buyer_debt") await scope.ServiceProvider.GetRequiredService<WalletService>().Charge(buyer.Player.PlayerId, 501, "storage", 0);
            if (kind == "forbidden") await scope.ServiceProvider.GetRequiredService<ForbiddenDefsService>().Set(["Steel"]);
            if (kind == "blocked")
                await new Protocol.V1.Social.SocialClient(f.CreateChannel()).BlockAsync(new PlayerRef { PlayerId = buyer.Player.PlayerId }, Bearer(seller.SessionToken));
        }
        var error = await Assert.ThrowsAsync<RpcException>(() => api.BidAsync(Bid(lot.Id), Bearer(buyer.SessionToken)).ResponseAsync);
        Assert.Equal(code, error.ErrorDetail()?.Code);
        Assert.Equal(0, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Bid).SingleAsync()));
        Assert.Equal(0, await Db(f, db => db.Parcels.CountAsync()));
        Assert.Equal(kind == "buyer_debt" ? -1 : 500, await Db(f, db => db.Wallets.Where(w => w.PlayerId == buyer.Player.PlayerId).Select(w => w.Balance).SingleAsync()));
    }

    [Theory]
    [InlineData("frozen", ErrorCode.PlayerFrozen)]
    [InlineData("threat", ErrorCode.UnderThreat)]
    [InlineData("debt", ErrorCode.InDebt)]
    [InlineData("forbidden", ErrorCode.DefForbidden)]
    public async Task PostingUsesTheExistingOutflowGates(string kind, ErrorCode code)
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var other = await f.RegisterAndLogin("other");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            if (kind == "frozen") await scope.ServiceProvider.GetRequiredService<PlayerFreeze>().Set(seller.Player.PlayerId, true, "test");
            if (kind == "debt") await scope.ServiceProvider.GetRequiredService<WalletService>().Charge(seller.Player.PlayerId, 1, "storage", 0);
            if (kind == "forbidden") await scope.ServiceProvider.GetRequiredService<ForbiddenDefsService>().Set(["Steel"]);
            if (kind == "threat")
            {
                db.Raids.Add(new Server.Storage.Entities.Raid { AttackerPlayerId = other.Player.PlayerId, TargetPlayerId = seller.Player.PlayerId,
                    State = RaidStateDb.Queued, Controller = (int)RaidController.ControllerAi, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7) });
                await db.SaveChangesAsync();
            }
        }
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        Assert.Equal(code, (await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(Post(), Bearer(seller.SessionToken)).ResponseAsync)).ErrorDetail()?.Code);
        Assert.Equal(0, await Db(f, db => db.Set<AuctionRow>().CountAsync()));
        Assert.Equal(0, await Db(f, db => db.Players.Where(p => p.Id == seller.Player.PlayerId).Select(p => p.LastOutgoingTick).SingleAsync()));
    }

    [Fact]
    public async Task ReplaysSucceedDuringFreezeAndSweepWaitsForThaw()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post();
        var lot = await api.PostAsync(post, Bearer(seller.SessionToken));
        await Fund(f, buyer.Player.PlayerId, 500);
        var bid = Bid(lot.Id);
        var placed = await api.BidAsync(bid, Bearer(buyer.SessionToken));
        var freeze = f.Services.GetRequiredService<FreezeState>(); freeze.Frozen = true;
        Assert.Equal(lot, await api.PostAsync(post, Bearer(seller.SessionToken)));
        Assert.Equal(placed, await api.BidAsync(bid, Bearer(buyer.SessionToken)));
        using var scope = f.Services.CreateScope();
        var auctions = scope.ServiceProvider.GetRequiredService<AuctionService>();
        Assert.Equal(0, await auctions.ExpireDue(DateTime.UtcNow.AddHours(2)));
        freeze.Frozen = false;
        var players = scope.ServiceProvider.GetRequiredService<PlayerFreeze>();
        await players.Set(buyer.Player.PlayerId, true, "test");
        Assert.Equal(0, await auctions.ExpireDue(DateTime.UtcNow.AddHours(2)));
        await players.Set(buyer.Player.PlayerId, false, null);
        Assert.Equal(1, await auctions.ExpireDue(DateTime.UtcNow.AddHours(2)));
    }

    [Fact]
    public async Task LoweredSizeLimitCannotRefuseAReplayOfAlreadyEscrowedGoods()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post(); post.Items.Items[0].ScribeXmlZstd = ByteString.CopyFrom(new byte[5000]);
        var accepted = await api.PostAsync(post, Bearer(seller.SessionToken));
        f.Services.GetRequiredService<IOptions<EconomyOptions>>().Value.MaxParcelBytes = 128;
        Assert.Equal(accepted, await api.PostAsync(post, Bearer(seller.SessionToken)));
        Assert.Equal(1, await Db(f, db => db.Set<AuctionRow>().CountAsync()));
    }

    [Fact]
    public async Task AlreadyEscrowedGoodsCanBeBidAndSettledAfterForbiddenListChangesOrSellerIsThreatened()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        await Fund(f, buyer.Player.PlayerId, 500);
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ForbiddenDefsService>().Set(["Steel"]);
        await Db(f, async db =>
        {
            db.Raids.Add(new Server.Storage.Entities.Raid { AttackerPlayerId = buyer.Player.PlayerId, TargetPlayerId = seller.Player.PlayerId,
                State = RaidStateDb.Queued, Controller = (int)RaidController.ControllerAi, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7) });
            return await db.SaveChangesAsync();
        });
        await api.BidAsync(Bid(lot.Id), Bearer(buyer.SessionToken));
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AuctionService>().ExpireDue(DateTime.UtcNow.AddHours(2)));
        Assert.Equal(400, await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(buyer.Player.PlayerId));
        Assert.Equal(buyer.Player.PlayerId, await Db(f, db => db.Parcels.Select(p => p.ToPlayerId).SingleAsync()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetCancelsAuctionWhenPlayerIsSellerOrHighestBidder(bool resetSeller)
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller"); var buyer = await f.RegisterAndLogin("buyer");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.SessionToken));
        await Fund(f, buyer.Player.PlayerId, 500);
        await api.BidAsync(Bid(lot.Id), Bearer(buyer.SessionToken));
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, resetSeller ? seller.Player.PlayerId : buyer.Player.PlayerId);
        Assert.Equal(500, await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(buyer.Player.PlayerId));
        Assert.Equal((int)AuctionStatus.AuctionCancelled, await Db(f, db => db.Set<AuctionRow>().Select(a => a.Status).SingleAsync()));
        Assert.Equal(seller.Player.PlayerId, await Db(f, db => db.Parcels.Select(p => p.ToPlayerId).SingleAsync()));
    }

    [Fact]
    public async Task ArchivingColonyReturnsBothEscrowsAndBlocksNewAuctionsUntilRestored()
    {
        using var f = HygieneServer();
        var seller = await Settled(f, "seller", 100); var buyer = await f.RegisterAndLogin("buyer");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var lot = await api.PostAsync(Post(), Bearer(seller.Token));
        await Fund(f, buyer.Player.PlayerId, 500);
        await api.BidAsync(Bid(lot.Id), Bearer(buyer.SessionToken));
        f.Services.GetRequiredService<SessionRegistry>().End(seller.Token);
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ColonyArchive>().Archive(seller.PlayerId, "test", Actor.Api);
        var auctions = scope.ServiceProvider.GetRequiredService<AuctionService>();
        Assert.Equal(ErrorCode.AuctionGone, (await Assert.ThrowsAsync<RpcException>(() => auctions.Post(seller.PlayerId, Post()))).ErrorDetail()?.Code);
        Assert.Equal(500, await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(buyer.Player.PlayerId));
        Assert.Equal(seller.PlayerId, await Db(f, db => db.Parcels.Select(p => p.ToPlayerId).SingleAsync()));
    }

    [Fact]
    public async Task ExistingLotLimitCoversBothMarketsAndPostingStampsOutgoingSave()
    {
        using var f = EconomyServer().WithSetting("Economy:MaxLotsPerPlayer", "1");
        var seller = await f.RegisterAndLogin("seller");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post(); post.GameTick = 1234;
        var lot = await api.PostAsync(post, Bearer(seller.SessionToken));
        Assert.Equal(1234, await Db(f, db => db.Players.Select(p => p.LastOutgoingTick).SingleAsync()));
        var market = new Protocol.V1.Market.MarketClient(f.CreateChannel());
        var fixedPrice = new PostRequest { ClientRequestId = "fixed", Items = Items(("WoodLog", 1)), Price = 100 };
        await Assert.ThrowsAsync<RpcException>(() => market.PostAsync(fixedPrice, Bearer(seller.SessionToken)).ResponseAsync);
        await api.CancelAsync(new AuctionCancelRequest { ClientRequestId = "cancel", AuctionId = lot.Id }, Bearer(seller.SessionToken));
        await market.PostAsync(fixedPrice, Bearer(seller.SessionToken));
        await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(Post(), Bearer(seller.SessionToken)).ResponseAsync);
    }

    [Theory]
    [InlineData(0, 0, 3600)]
    [InlineData(100, 99, 3600)]
    [InlineData(100, -1, 3600)]
    [InlineData(100, 0, 59)]
    [InlineData(100, 0, 2592001)]
    public async Task InvalidPricesAndDurationNeverCreateEscrow(long start, long buyout, int duration)
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post(); post.StartPrice = start; post.BuyoutPrice = buyout; post.DurationSeconds = duration;
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(post, Bearer(seller.SessionToken)).ResponseAsync)).StatusCode);
        Assert.Equal(0, await Db(f, db => db.Set<AuctionRow>().CountAsync()));
    }

    [Fact]
    public async Task AnonymousAndOversizedRequestsAreRefusedAndListNeverContainsBlobs()
    {
        using var f = EconomyServer().WithSetting("Economy:MaxParcelBytes", "128");
        var seller = await f.RegisterAndLogin("seller");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(Post()).ResponseAsync)).StatusCode);
        var huge = Post(); huge.Items.Items[0].ScribeXmlZstd = ByteString.CopyFrom(new byte[129]);
        Assert.Equal(ErrorCode.ParcelTooBig, (await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(huge, Bearer(seller.SessionToken)).ResponseAsync)).ErrorDetail()?.Code);
        await api.PostAsync(Post(), Bearer(seller.SessionToken));
        var list = await api.ListAsync(new AuctionQuery(), Bearer(seller.SessionToken));
        Assert.Empty(Assert.Single(list.Auctions).Items.Items[0].ScribeXmlZstd);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => api.ListAsync(new AuctionQuery { Page = int.MaxValue }, Bearer(seller.SessionToken)).ResponseAsync)).StatusCode);
    }

    [Fact]
    public async Task DeclaredValueIsBoundedBeforeItCanOverflowWealthOrStorageFees()
    {
        using var f = EconomyServer();
        var seller = await f.RegisterAndLogin("seller");
        var api = new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel());
        var post = Post();
        post.Items.Items.Clear();
        for (int i = 0; i < 10; i++) post.Items.Items.Add(Item("Steel", int.MaxValue, marketValueX100: long.MaxValue));
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => api.PostAsync(post, Bearer(seller.SessionToken)).ResponseAsync)).StatusCode);
        Assert.Equal(0, await Db(f, db => db.Set<AuctionRow>().CountAsync()));
    }

    [Fact]
    public async Task AdminRestNeedsScopeAndCancellationIsAudited()
    {
        using var f = EconomyServer().WithSetting("Admin:ApiToken", "admin-test");
        var seller = await f.RegisterAndLogin("seller");
        var lot = await new Protocol.V1.Auctions.AuctionsClient(f.CreateChannel()).PostAsync(Post(), Bearer(seller.SessionToken));
        using var http = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/admin/auctions/")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-test");
        (await http.GetAsync("/api/admin/auctions/")).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/api/admin/auctions/{lot.Id}/cancel", new { reason = "test" })).EnsureSuccessStatusCode();
        Assert.Equal(1, await Db(f, db => db.AdminActions.CountAsync(a => a.Action == "auction_cancel")));
    }
}
