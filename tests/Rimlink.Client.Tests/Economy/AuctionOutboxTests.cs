using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Client.Tests.Support;
using Rimlink.Client.State;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;

namespace Rimlink.Client.Tests.Economy;

public sealed class AuctionOutboxTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "rimlink-auction-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    private TransferOutbox Open() => new(Path.Combine(dir, "outbox.json"), new ConsoleLog());

    [Fact]
    public async Task LostPostBidAndCancelAnswersSurviveDiskReloadAndReplay()
    {
        using var f = EconomyServer();
        await using var seller = await ClientFixtures.Connected(f, "seller");
        await using var buyer = await ClientFixtures.Connected(f, "buyer");
        await Fund(f, buyer.Login!.Player.PlayerId, 500);
        var s = new EconomyClient(seller);
        var b = new EconomyClient(buyer);
        var post = new AuctionPostRequest { ClientRequestId = EconomyClient.NewRequestId(), Items = Items(("Steel", 2)), StartPrice = 100, DurationSeconds = 3600 };
        Open().Add(post);
        var lot = await s.PostAuction(post, CancellationToken.None); // applied, response lost before entry removed
        Assert.Equal(1, (await Open().Replay(s, CancellationToken.None)).Completed);
        var bid = new AuctionBidRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = lot.Id, Amount = 100 };
        Open().Add(bid);
        await b.BidAuction(bid, CancellationToken.None);
        Assert.Equal(1, (await Open().Replay(b, CancellationToken.None)).Completed);
        Assert.Equal(400, (await b.Wallet(CancellationToken.None)).Balance);
        var second = post.Clone(); second.ClientRequestId = EconomyClient.NewRequestId();
        var empty = await s.PostAuction(second, CancellationToken.None);
        var cancel = new AuctionCancelRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = empty.Id };
        Open().Add(cancel);
        await s.CancelAuction(cancel, CancellationToken.None);
        Assert.Equal(1, (await Open().Replay(s, CancellationToken.None)).Completed);
        Assert.Empty(Open().Entries);
        using var scope = f.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Set<AuctionRow>().CountAsync());
    }

    [Fact]
    public async Task RefusedGoodsStayForReturnButRefusedBidHoldsNoPhysicalGoods()
    {
        using var f = EconomyServer();
        await using var seller = await ClientFixtures.Connected(f, "seller");
        var box = Open();
        box.Add(new AuctionPostRequest { ClientRequestId = EconomyClient.NewRequestId(), Items = Items(("Steel", 2)), StartPrice = -1, DurationSeconds = 3600 });
        box.Add(new AuctionBidRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = 999, Amount = 100 });
        box.Add(new AuctionCancelRequest { ClientRequestId = EconomyClient.NewRequestId(), AuctionId = 999 });
        var report = await box.Replay(new EconomyClient(seller), CancellationToken.None);
        Assert.Equal(3, report.Refused);
        var entry = Assert.Single(Open().Entries);
        Assert.IsType<AuctionPostRequest>(TransferOutbox.Decode(entry));
        Assert.NotNull(entry.Refusal);
    }

    [Fact]
    public void AuctionEventsInvalidateWealthAndNotifyOpenTab()
    {
        var state = new SharedState();
        int changed = 0;
        state.AuctionChanged += _ => changed++;
        state.Apply(new ServerEvent { AuctionUpdated = new AuctionUpdated { Auction = new Auction { Id = 1 } } });
        Assert.Equal(1, changed);
        Assert.True(state.Economy.WealthStale);
    }

    [Fact]
    public async Task RateLimitedReplayKeepsAcceptedGoodsPendingInsteadOfReturningThem()
    {
        using var f = EconomyServer().WithRateLimits();
        await using var seller = await ClientFixtures.Connected(f, "seller");
        var client = new EconomyClient(seller);
        var request = new AuctionPostRequest { ClientRequestId = EconomyClient.NewRequestId(), Items = Items(("Steel", 2)), StartPrice = 100, DurationSeconds = 3600 };
        await client.PostAuction(request, CancellationToken.None);
        var box = Open(); box.Add(request);
        var report = await box.Replay(client, CancellationToken.None);
        Assert.Equal(1, report.Pending);
        Assert.Equal(0, report.Refused);
        Assert.Null(Assert.Single(Open().Entries).Refusal);
    }
}
