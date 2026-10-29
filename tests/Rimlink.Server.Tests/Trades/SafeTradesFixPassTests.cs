using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SocialFixtures;

namespace Rimlink.Server.Tests.Trades;

/// <summary>Findings of the safe trades final review, each pinned by a test.</summary>
public class SafeTradesFixPassTests
{
    private sealed record Party(long Id, SocialClients C, Metadata Auth);

    private static async Task<Party> Join(ServerFixture fixture, string login, string name)
    {
        var reply = await fixture.RegisterAndLogin(login, playerName: name);
        await Fund(fixture, reply.Player.PlayerId, 100);
        return new Party(reply.Player.PlayerId, SocialFixtures.Social(fixture, reply.SessionToken), Bearer(reply.SessionToken));
    }

    private static async Task<long> Balance(Party p) => (await p.C.Economy.Wallet.GetAsync(new Empty(), p.Auth)).Balance;

    [Fact]
    public async Task RefundOfAnAwaitingGiftClosesTheDecision()
    {
        using var fixture = EconomyServer().WithSetting("Auth:AdminLogins:0", "admin");
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), bob.Auth);
        var gift = await nova.C.Economy.Transfer.SendAsync(Send(bob.Id, Items(("Steel", 10)), silver: 50), nova.Auth);
        using var scope = fixture.Services.CreateScope();
        var actor = new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
        Assert.True(await scope.ServiceProvider.GetRequiredService<AdminOperations>().RefundParcel(actor, gift.Id));

        Assert.Equal(gift.Id, await ReadEvent(bobStream, ServerEvent.BodyOneofCase.GiftRefused, e => e.GiftRefused.ParcelId));
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Parcels.Where(p => p.Id == gift.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTime.UtcNow.AddDays(-8)));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<GiftService>().RefuseOverdue(DateTime.UtcNow));
        await bob.C.Economy.Mailbox.AnswerAsync(new ParcelAnswer { ParcelId = gift.Id, Accept = false }, bob.Auth);
        await bob.C.Economy.Mailbox.AnswerAsync(new ParcelAnswer { ParcelId = gift.Id, Accept = true }, bob.Auth);

        Assert.Equal(100, await Balance(nova));
        Assert.Single((await nova.C.Economy.Mailbox.ListAsync(new Empty(), nova.Auth)).Parcels);
        Assert.Empty((await bob.C.Economy.Mailbox.ListAsync(new Empty(), bob.Auth)).Parcels);
        Assert.Equal(Rimlink.Server.Storage.Entities.ParcelDecisionDb.Refused, (await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == gift.Id)).Decision);
    }

    [Fact]
    public async Task AwaitingGiftBlobIsNotHandedOut()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var gift = await nova.C.Economy.Transfer.SendAsync(Send(bob.Id, Items(("Steel", 10))), nova.Auth);

        var hidden = await Assert.ThrowsAsync<RpcException>(() => bob.C.Economy.Mailbox.GetAsync(new ParcelRef { ParcelId = gift.Id }, bob.Auth).ResponseAsync);
        await bob.C.Economy.Mailbox.AnswerAsync(new ParcelAnswer { ParcelId = gift.Id, Accept = true }, bob.Auth);
        var full = await bob.C.Economy.Mailbox.GetAsync(new ParcelRef { ParcelId = gift.Id }, bob.Auth);

        Assert.Equal(StatusCode.NotFound, hidden.StatusCode);
        Assert.False(full.Items.Items[0].ScribeXmlZstd.IsEmpty);
    }

    [Fact]
    public async Task SetSideConfirmsOnlyTheRevisionItWasMadeOn()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, silver: 50), nova.Auth);
        var bobSide = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Plasteel", 5)), baseRevision: trade.Revision), bob.Auth);
        Assert.True(bobSide.PartnerSide.Confirmed);
        var novaTakesBack = await nova.C.Trades.SetSideAsync(TradeSide(trade.Id, silver: 0, baseRevision: bobSide.Revision), nova.Auth);

        // Bob's window was opened at revision 2: he never saw Nova take her silver back.
        var stale = await bob.C.Trades.SetSideAsync(TradeSide(trade.Id, Items(("Gold", 1)), baseRevision: bobSide.Revision), bob.Auth);
        var afterNova = await nova.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = stale.Revision }, nova.Auth);

        Assert.Equal(novaTakesBack.Revision + 1, stale.Revision);
        Assert.False(stale.PartnerSide.Confirmed);
        Assert.Equal(TradeStatus.TradeOpen, afterNova.Status);
        Assert.Equal(100, await Balance(nova));
    }

    [Fact]
    public async Task OpenLeavesTheInitiatorUnconfirmed()
    {
        using var fixture = EconomyServer();
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        var trade = await nova.C.Trades.OpenAsync(OpenTrade(bob.Id, Items(("Steel", 10)), note: "what will you give?"), nova.Auth);

        var bobConfirms = await bob.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = trade.Revision }, bob.Auth);

        Assert.False(trade.InitiatorSide.Confirmed);
        Assert.Equal(TradeStatus.TradeOpen, bobConfirms.Status);
        Assert.Empty((await bob.C.Economy.Mailbox.ListAsync(new Empty(), bob.Auth)).Parcels);
        var novaConfirms = await nova.C.Trades.ConfirmAsync(new TradeConfirmRequest { TradeId = trade.Id, Revision = trade.Revision }, nova.Auth);
        Assert.Equal(TradeStatus.TradeCompleted, novaConfirms.Status);
    }

    [Fact]
    public async Task AwaitingGiftsDoNotFillTheMailboxForOtherParcels()
    {
        using var fixture = EconomyServer().WithSetting("Economy:MaxPendingParcels", "2");
        var nova = await Join(fixture, "nova", "Nova");
        var bob = await Join(fixture, "bob", "Bob");
        await nova.C.Economy.Transfer.SendAsync(Send(bob.Id, Items(("Steel", 1))), nova.Auth);
        await nova.C.Economy.Transfer.SendAsync(Send(bob.Id, Items(("Steel", 1))), nova.Auth);

        var withdrawal = await bob.C.Economy.Wallet.WithdrawAsync(new WithdrawRequest { Silver = 10 }, bob.Auth);
        var third = await Assert.ThrowsAsync<RpcException>(() => nova.C.Economy.Transfer.SendAsync(Send(bob.Id, Items(("Steel", 1))), nova.Auth).ResponseAsync);

        Assert.True(withdrawal.Id > 0);
        Assert.Contains("mailbox is full", third.Status.Detail);
    }
}
