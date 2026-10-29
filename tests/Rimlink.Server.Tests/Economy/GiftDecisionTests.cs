using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Economy;

public class GiftDecisionTests
{
    private static Task Answer(EconomyClients c, long parcelId, bool accept) =>
        c.Mailbox.AnswerAsync(new ParcelAnswer { ParcelId = parcelId, Accept = accept }, c.Auth).ResponseAsync;

    [Fact]
    public async Task GiftAwaitsADecisionAndCannotBeDelivered()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);

        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10))), n.Auth);

        var got = Assert.Single((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels);
        Assert.True(got.AwaitingDecision);
        var ex = await Assert.ThrowsAsync<RpcException>(() => b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 100 }, b.Auth).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task AcceptedGiftIsDeliveredNormally()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), b.Auth);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10))), n.Auth);
        Assert.True(await ReadEvent(bobStream, ServerEvent.BodyOneofCase.ParcelArrived, e => e.ParcelArrived.Parcel.AwaitingDecision));

        await Answer(b, parcel.Id, accept: true);

        Assert.False(await ReadEvent(bobStream, ServerEvent.BodyOneofCase.ParcelArrived, e => e.ParcelArrived.Parcel.AwaitingDecision));
        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 100 }, b.Auth);
        await Answer(b, parcel.Id, accept: false);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(100, (await db.Parcels.SingleAsync(p => p.Id == parcel.Id)).DeliveredTick);
        Assert.Equal(1, await db.Parcels.CountAsync());
    }

    [Fact]
    public async Task RefusedGiftComesBackToTheSender()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Fund(fixture, nova.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), n.Auth);
        using var bobStream = events.Subscribe(new Empty(), b.Auth);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10)), Pawns(("Colonist", "Ann")), silver: 50), n.Auth);
        double refusedBefore = EconomyMetrics.GiftsRefused.Value;

        await Answer(b, parcel.Id, accept: false);

        Assert.Empty((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels);
        var back = Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
        Assert.Equal(ParcelKind.ParcelGiftReturn, back.Kind);
        Assert.Equal("Bob", back.From.Name);
        Assert.Equal(10, back.Items.Items[0].Count);
        Assert.Equal("Ann", back.Pawns.Pawns[0].Name);
        Assert.Equal(0, back.Silver);
        Assert.False(back.AwaitingDecision);
        Assert.Equal(100, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        var toNova = await ReadEvent(novaStream, ServerEvent.BodyOneofCase.GiftRefused, e => e.GiftRefused);
        Assert.Equal(parcel.Id, toNova.ParcelId);
        Assert.Equal("Bob", toNova.By.Name);
        Assert.False(toNova.Expired);
        Assert.Contains("Steel", toNova.Returned);
        Assert.Equal(parcel.Id, await ReadEvent(bobStream, ServerEvent.BodyOneofCase.GiftRefused, e => e.GiftRefused.ParcelId));
        Assert.Equal(refusedBefore + 1, EconomyMetrics.GiftsRefused.Value);
    }

    [Fact]
    public async Task SilverOnlyGiftRefusalCreatesNoParcel()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await Fund(fixture, nova.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, silver: 30), n.Auth);

        await Answer(b, parcel.Id, accept: false);

        Assert.Empty((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
        Assert.Equal(100, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
    }

    [Fact]
    public async Task AcceptGiftsFlagSkipsTheDecision()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await SocialFixtures.AcceptGifts(fixture, bob.Player.PlayerId);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);

        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10))), n.Auth);

        Assert.False(Assert.Single((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels).AwaitingDecision);
        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 100 }, b.Auth);
    }

    [Fact]
    public async Task OverdueGiftsAreRefusedBySweep()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = Clients(fixture, nova.SessionToken);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var novaStream = events.Subscribe(new Empty(), n.Auth);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10))), n.Auth);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        await db.Parcels.Where(p => p.Id == parcel.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, DateTime.UtcNow.AddDays(-8)));

        int refused = await scope.ServiceProvider.GetRequiredService<GiftService>().RefuseOverdue(DateTime.UtcNow);

        Assert.Equal(1, refused);
        Assert.Equal(ParcelKind.ParcelGiftReturn, Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels).Kind);
        Assert.True(await ReadEvent(novaStream, ServerEvent.BodyOneofCase.GiftRefused, e => e.GiftRefused.Expired));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<GiftService>().RefuseOverdue(DateTime.UtcNow));
    }

    [Fact]
    public async Task OnlyTheRecipientMayAnswer()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var carol = await fixture.RegisterAndLogin("carol", playerName: "Carol");
        await Fund(fixture, nova.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10))), n.Auth);
        var withdrawal = await n.Wallet.WithdrawAsync(new WithdrawRequest { Silver = 10 }, n.Auth);

        var byCarol = await Assert.ThrowsAsync<RpcException>(() => Answer(Clients(fixture, carol.SessionToken), parcel.Id, accept: false));
        var noDecision = await Assert.ThrowsAsync<RpcException>(() => Answer(n, withdrawal.Id, accept: false));

        Assert.Equal(StatusCode.NotFound, byCarol.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, noDecision.StatusCode);
    }

    [Fact]
    public async Task RefusingAGiftOfAResetSenderReturnsItToTheirMailbox()
    {
        using var fixture = EconomyServer().WithSetting("Auth:AdminLogins:0", "admin");
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = Clients(fixture, nova.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10))), n.Auth);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin"), nova.Player.PlayerId);

        await Answer(Clients(fixture, bob.SessionToken), parcel.Id, accept: false);

        var again = await fixture.RegisterAndLogin("nova");
        var back = Assert.Single((await n.Mailbox.ListAsync(new Empty(), Bearer(again.SessionToken))).Parcels);
        Assert.Equal(ParcelKind.ParcelGiftReturn, back.Kind);
    }

    [Fact]
    public async Task GiftReturnRedeliversWhenTheSendersSaveIsOlder()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        var n = Clients(fixture, nova.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 10)), tick: 500), n.Auth);
        await Answer(Clients(fixture, bob.SessionToken), parcel.Id, accept: false);
        var back = Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
        await n.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = back.Id, GameTick = 900 }, n.Auth);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] older = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 800);
        await Upload(saves, nova.SessionToken, older, HeaderFor(older, ServerFixture.WorldSeed, "Nova", tick: 800));

        var again = Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);

        Assert.Equal(back.Id, again.Id);
        Assert.True(again.Redeliver);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Parcels.CountAsync(p => p.ToPlayerId == nova.Player.PlayerId));
    }
}
