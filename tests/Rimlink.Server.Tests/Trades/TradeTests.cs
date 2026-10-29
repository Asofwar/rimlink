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

public class TradeTests
{
    private sealed record Party(long Id, SocialClients C, Metadata Auth);

    private static async Task<Party> Join(ServerFixture fixture, string login, string name, long funds = 100)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: name);
        if (funds > 0) await Fund(fixture, reply.Player.PlayerId, funds);
        return new Party(reply.Player.PlayerId, SocialFixtures.Social(fixture, reply.SessionToken), Bearer(reply.SessionToken));
    }

    private static async Task<long> Balance(Party p) => (await p.C.Economy.Wallet.GetAsync(new Empty(), p.Auth)).Balance;

    private static async Task<List<Parcel>> Mailbox(Party p) => (await p.C.Economy.Mailbox.ListAsync(new Empty(), p.Auth)).Parcels.ToList();

    private static async Task<ErrorCode?> Refusal(Func<Task> call) => (await Assert.ThrowsAsync<RpcException>(call)).ErrorDetail()?.Code;

    [Fact]
    public async Task OpenEscrowsTheInitiatorsSide()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), bob.Auth);
        double opened = TradeMetrics.Outcomes.WithLabels("opened").Value;

        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), Pawns(("Colonist", "Ann")), silver: 30, note: "for your rifle", tick: 640), nova.Auth);

        Assert.Equal(TradeStatus.TradeOpen, trade.Status);
        Assert.Equal(1, trade.Revision);
        Assert.False(trade.InitiatorSide.Confirmed); // an offer agrees to nothing until its maker confirms
        Assert.False(trade.PartnerSide.Confirmed);
        Assert.Equal("for your rifle", trade.InitiatorSide.Note);
        Assert.Equal("Bob", trade.Partner.Name);
        Assert.Equal(70, await Balance(nova));
        var pushed = await ReadEvent(bobStream, ServerEvent.BodyOneofCase.TradeUpdated, e => e.TradeUpdated.Trade);
        Assert.Equal(trade.Id, pushed.Id);
        Assert.Equal(10, pushed.InitiatorSide.Items.Items[0].Count);
        Assert.True(pushed.InitiatorSide.Items.Items[0].ScribeXmlZstd.IsEmpty);
        Assert.Equal(opened + 1, TradeMetrics.Outcomes.WithLabels("opened").Value);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(640, (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.SingleAsync(p => p.Id == nova.Id)).LastOutgoingTick);
    }

    [Fact]
    public async Task OpenRefusals()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MaxOpenTrades", "2");
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var carol = await Join(fixture, "carol", "Carol");
        var dan = await Join(fixture, "dan", "Dan");
        await carol.C.Social.SetTradesClosedAsync(new FlagRequest { On = true }, carol.Auth);
        await dan.C.Social.BlockAsync(new PlayerRef { PlayerId = nova.Id }, dan.Auth);

        Assert.Equal(ErrorCode.SelfTrade, await Refusal(() => nova.C.Trades.OpenAsync(OpenTrade(nova.Id, silver: 1), nova.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.TradesClosed, await Refusal(() => nova.C.Trades.OpenAsync(OpenTrade(carol.Id, silver: 1), nova.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.Blocked, await Refusal(() => nova.C.Trades.OpenAsync(OpenTrade(dan.Id, silver: 1), nova.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.RecipientUnknown, await Refusal(() => nova.C.Trades.OpenAsync(OpenTrade(9999, silver: 1), nova.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.WalletInsufficient, await Refusal(() => nova.C.Trades.OpenAsync(OpenTrade(bob.Id, silver: 500), nova.Auth).ResponseAsync));
        var longNote = await Assert.ThrowsAsync<RpcException>(() => nova.C.Trades.OpenAsync(OpenTrade(bob.Id, note: new string('x', 201)), nova.Auth).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, longNote.StatusCode);
        Assert.Equal(100, await Balance(nova));

        await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, note: "one"), nova.Auth);
        await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, note: "two"), nova.Auth);
        Assert.Equal(ErrorCode.TradeLimit, await Refusal(() => nova.C.Trades.OpenAsync(OpenTrade(bob.Id, silver: 5), nova.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.TradeLimit, await Refusal(() => dan.C.Trades.OpenAsync(OpenTrade(bob.Id, silver: 5), dan.Auth).ResponseAsync));
        Assert.Equal(100, await Balance(nova));
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Trades.CountAsync());
    }

    [Fact]
    public async Task SameOpenRequestIdReturnsTheSameTrade()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var request = OpenTrade(bob.Id, Items(("Steel", 10)), silver: 30);

        var first = await nova.C.Trades.OpenAsync(request, nova.Auth);
        var again = await nova.C.Trades.OpenAsync(request, nova.Auth);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(70, await Balance(nova));
    }

    [Fact]
    public async Task SetSideReplacingReturnsThePreviousGoods()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10))), nova.Auth);

        var second = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5)), silver: 20), bob.Auth);
        Assert.Equal(2, second.Revision);
        Assert.Empty(await Mailbox(bob));
        var third = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Gold", 3)), silver: 10, returnGoods: true, baseRevision: second.Revision), bob.Auth);

        var back = Assert.Single(await Mailbox(bob));
        Assert.Equal(ParcelKind.ParcelTradeReturn, back.Kind);
        Assert.Equal("Plasteel", back.Items.Items.Single().DefName);
        Assert.Equal(0, back.Silver);
        Assert.Equal(90, await Balance(bob));
        Assert.Equal(3, third.Revision);
        Assert.Equal("Gold", third.PartnerSide.Items.Items.Single().DefName);
        Assert.Equal(10, third.PartnerSide.Silver);
        Assert.True(third.PartnerSide.Confirmed);
        Assert.False(third.InitiatorSide.Confirmed);
    }

    [Fact]
    public async Task SetSideAddingKeepsTheEscrow()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), silver: 30), nova.Auth);

        var added = await nova.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Steel", 5)), silver: 40, baseRevision: trade.Revision), nova.Auth);

        Assert.Equal(15, added.InitiatorSide.Items.Items.Where(i => i.DefName == "Steel").Sum(i => i.Count));
        Assert.Equal(40, added.InitiatorSide.Silver);
        Assert.Equal(60, await Balance(nova));
        Assert.Empty(await Mailbox(nova));
        var confirmed = await bob.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = added.Revision }, bob.Auth);
        Assert.Equal(TradeStatus.TradeCompleted, confirmed.Status);
    }

    [Fact]
    public async Task NoteOnlyChangeMovesNothingAndUnconfirmsTheOtherSide()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), silver: 30), nova.Auth);
        var bobSide = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, note: "what about a rifle?", baseRevision: trade.Revision), bob.Auth);
        Assert.True(bobSide.PartnerSide.Confirmed);

        var noted = await nova.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 30, note: "no rifles"), nova.Auth);

        Assert.Equal(bobSide.Revision + 1, noted.Revision);
        Assert.Equal(10, noted.InitiatorSide.Items.Items.Single().Count);
        Assert.Equal("no rifles", noted.InitiatorSide.Note);
        Assert.False(noted.PartnerSide.Confirmed);
        Assert.Equal(70, await Balance(nova));
        Assert.Empty(await Mailbox(nova));
    }

    [Fact]
    public async Task ConfirmOfAnOldRevisionIsTradeChanged()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10))), nova.Auth);
        var changed = await nova.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Steel", 1)), returnGoods: true), nova.Auth);

        Assert.Equal(ErrorCode.TradeChanged, await Refusal(() => bob.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = 1 }, bob.Auth).ResponseAsync));

        var now = Assert.Single((await bob.C.Trades.ListAsync(new Empty(), bob.Auth)).Trades);
        Assert.Equal(changed.Revision, now.Revision);
        Assert.Equal(TradeStatus.TradeOpen, now.Status);
        Assert.False(now.PartnerSide.Confirmed);
    }

    [Fact]
    public async Task ExchangeAfterBothConfirm()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), nova.Auth);
        double completed = TradeMetrics.Outcomes.WithLabels("completed").Value;
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), Pawns(("Colonist", "Ann")), silver: 30), nova.Auth);
        var bobSide = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5)), silver: 20, baseRevision: trade.Revision), bob.Auth);

        var done = await nova.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = bobSide.Revision }, nova.Auth);

        Assert.Equal(TradeStatus.TradeCompleted, done.Status);
        var toNova = Assert.Single(await Mailbox(nova));
        var toBob = Assert.Single(await Mailbox(bob));
        Assert.Equal(ParcelKind.ParcelTrade, toNova.Kind);
        Assert.Equal("Bob", toNova.From.Name);
        Assert.Equal("Plasteel", toNova.Items.Items.Single().DefName);
        Assert.Equal(20, toNova.Silver);
        Assert.Equal("Nova", toBob.From.Name);
        Assert.Equal("Ann", toBob.Pawns.Pawns.Single().Name);
        Assert.Equal(30, toBob.Silver);
        Assert.False(toNova.AwaitingDecision);
        await nova.C.Economy.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = toNova.Id, GameTick = 200 }, nova.Auth);
        await bob.C.Economy.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = toBob.Id, GameTick = 200 }, bob.Auth);
        Assert.Equal(90, await Balance(nova));
        Assert.Equal(110, await Balance(bob));
        var pushed = new List<TradeStatus>();
        for (int i = 0; i < 3; i++) pushed.Add(await ReadEvent(novaStream, ServerEvent.BodyOneofCase.TradeUpdated, e => e.TradeUpdated.Trade.Status));
        Assert.Equal([TradeStatus.TradeOpen, TradeStatus.TradeOpen, TradeStatus.TradeCompleted], pushed);
        Assert.Equal(completed + 1, TradeMetrics.Outcomes.WithLabels("completed").Value);
    }

    [Fact]
    public async Task SecondConfirmAfterTheExchangeReturnsTheCompletedTrade()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10))), nova.Auth);
        var bobSide = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5)), baseRevision: trade.Revision), bob.Auth);
        var confirm = new TradeConfirmRequest { TradeId = trade.Id, Revision = bobSide.Revision };
        await nova.C.Trades.ConfirmAsync(confirm, nova.Auth);

        var again = await nova.C.Trades.ConfirmAsync(confirm, nova.Auth);
        var bobAgain = await bob.C.Trades.ConfirmAsync(confirm, bob.Auth);

        Assert.Equal(TradeStatus.TradeCompleted, again.Status);
        Assert.Equal(TradeStatus.TradeCompleted, bobAgain.Status);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.CountAsync());
    }

    [Fact]
    public async Task ThirdPlayerCannotTouchTheTrade()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var carol = await Join(fixture, "carol", "Carol");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10))), nova.Auth);

        Assert.Equal(ErrorCode.TradeGone, await Refusal(() => carol.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 5), carol.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.TradeGone, await Refusal(() => carol.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = 1 }, carol.Auth).ResponseAsync));
        Assert.Equal(ErrorCode.TradeGone, await Refusal(() => carol.C.Trades.CancelAsync(new TradeRef { TradeId = trade.Id }, carol.Auth).ResponseAsync));
        Assert.Empty((await carol.C.Trades.ListAsync(new Empty(), carol.Auth)).Trades);
        Assert.Equal(100, await Balance(carol));
    }

    [Fact]
    public async Task ListShowsOpenAndRecentlyClosed()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var open = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, note: "open"), nova.Auth);
        var recent = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, note: "recent"), nova.Auth);
        var old = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, note: "old"), nova.Auth);
        await nova.C.Trades.CancelAsync(new TradeRef { TradeId = recent.Id }, nova.Auth);
        await nova.C.Trades.CancelAsync(new TradeRef { TradeId = old.Id }, nova.Auth);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
            await db.Trades.Where(t => t.Id == recent.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClosedAt, DateTime.UtcNow.AddDays(-2)));
            await db.Trades.Where(t => t.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ClosedAt, DateTime.UtcNow.AddDays(-8)));
        }

        var list = (await bob.C.Trades.ListAsync(new Empty(), bob.Auth)).Trades;

        Assert.Equal([recent.Id, open.Id], list.Select(t => t.Id));
    }
}
