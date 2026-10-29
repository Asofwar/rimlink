using Grpc.Core;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Economy;

public class MailboxTests
{
    [Fact]
    public async Task DeliverIsIdempotent()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await SocialFixtures.AcceptGifts(fixture, bob.Player.PlayerId);
        var parcel = await Clients(fixture, nova.SessionToken).Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 3))), Bearer(nova.SessionToken));
        var c = Clients(fixture, bob.SessionToken);

        await c.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 900 }, c.Auth);
        await c.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 950 }, c.Auth);
        // Until a save made after the delivery reaches the server, List keeps offering the parcel for redelivery.
        Assert.True(Assert.Single((await c.Mailbox.ListAsync(new Empty(), c.Auth)).Parcels).Redeliver);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "bob", tick: 1000);
        await Upload(saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "bob", tick: 1000));

        Assert.Empty((await c.Mailbox.ListAsync(new Empty(), c.Auth)).Parcels);
    }

    [Fact]
    public async Task DeliverCreditsSilverToTheRecipient()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await SocialFixtures.AcceptGifts(fixture, bob.Player.PlayerId);
        await Fund(fixture, nova.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);

        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, silver: 100), n.Auth);
        Assert.Equal(0, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        Assert.Equal(0, (await b.Wallet.GetAsync(new Empty(), b.Auth)).Balance);

        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 10 }, b.Auth);

        Assert.Equal(100, (await b.Wallet.GetAsync(new Empty(), b.Auth)).Balance);
    }

    [Fact]
    public async Task ListMarksParcelsDeliveredAfterTheClientsSaveForRedelivery()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await SocialFixtures.AcceptGifts(fixture, bob.Player.PlayerId);
        var parcel = await Clients(fixture, nova.SessionToken).Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 3))), Bearer(nova.SessionToken));
        var b = Clients(fixture, bob.SessionToken);
        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 900 }, b.Auth);
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] save = MakeSave(ServerFixture.WorldSeed, "Bob", tick: 800);
        await Upload(saves, bob.SessionToken, save, HeaderFor(save, ServerFixture.WorldSeed, "Bob", tick: 800));

        var list = await b.Mailbox.ListAsync(new Empty(), b.Auth);

        var again = Assert.Single(list.Parcels);
        Assert.True(again.Redeliver);
        Assert.Equal(900, again.DeliveredTick);
    }

    [Fact]
    public async Task DeliveringSomeoneElsesParcelIsNotFound()
    {
        using var fixture = EconomyServer();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var eve = await fixture.RegisterAndLogin("eve");
        var parcel = await Clients(fixture, nova.SessionToken).Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 3))), Bearer(nova.SessionToken));
        var e = Clients(fixture, eve.SessionToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() => e.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 1 }, e.Auth).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }
}
