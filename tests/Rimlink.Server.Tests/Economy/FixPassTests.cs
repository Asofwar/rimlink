using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using Role = Rimlink.Server.Storage.Entities.Role;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.SaveFixtures;

namespace Rimlink.Server.Tests.Economy;

/// <summary>Findings of the final review, each pinned by a test that failed first.</summary>
public class FixPassTests
{
    private static ServerFixture Server() => EconomyServer().WithSetting("Auth:AdminLogins:0", "admin");

    private static async Task<Actor> Admin(ServerFixture fixture)
    {
        var admin = await fixture.RegisterAndLogin("admin");
        return new Actor(await fixture.AccountIdOf("admin"), admin.Player.PlayerId, Role.Admin, "admin");
    }

    [Fact]
    public async Task ResetPlayerThenUploadOfAFreshColonySucceeds()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1)), tick: 1_800_000), n.Auth);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(actor, nova.Player.PlayerId);

        var again = await fixture.RegisterAndLogin("nova");
        var saves = new Protocol.V1.Saves.SavesClient(fixture.CreateChannel());
        byte[] fresh = MakeSave(ServerFixture.WorldSeed, "Nova", tick: 10);
        var info = await Upload(saves, again.SessionToken, fresh, HeaderFor(fresh, ServerFixture.WorldSeed, "Nova", tick: 10));

        Assert.Equal(1, info.Version);
    }

    [Fact]
    public async Task ResetPlayerDoesNotRedeliverOldParcels()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var parcel = await Clients(fixture, nova.SessionToken).Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), Bearer(nova.SessionToken));
        var b = Clients(fixture, bob.SessionToken);
        await b.Mailbox.DeliverAsync(new DeliverRequest { ParcelId = parcel.Id, GameTick = 5000 }, b.Auth);
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(actor, bob.Player.PlayerId);

        var again = await fixture.RegisterAndLogin("bob");
        Assert.Empty((await Clients(fixture, again.SessionToken).Mailbox.ListAsync(new Empty(), Bearer(again.SessionToken))).Parcels);
    }

    [Fact]
    public async Task ListWithThreeLargeParcelsSucceedsAndCarriesNoBlobs()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        for (int i = 0; i < 3; i++)
        {
            var request = Send(bob.Player.PlayerId);
            request.Items.Items.Add(Item("Steel", 1, blobBytes: 400 * 1024));
            await n.Transfer.SendAsync(request, n.Auth);
        }
        var b = Clients(fixture, bob.SessionToken);

        var list = await b.Mailbox.ListAsync(new Empty(), b.Auth);

        Assert.Equal(3, list.Parcels.Count);
        Assert.All(list.Parcels, p => Assert.Empty(p.Items.Items[0].ScribeXmlZstd));
        Assert.All(list.Parcels, p => Assert.Equal("Steel", p.Items.Items[0].DefName));
        var full = await b.Mailbox.GetAsync(new ParcelRef { ParcelId = list.Parcels[0].Id }, b.Auth);
        Assert.Equal(400 * 1024, full.Items.Items[0].ScribeXmlZstd.Length);
        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Mailbox.GetAsync(new ParcelRef { ParcelId = list.Parcels[0].Id }, n.Auth).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task MarketListCarriesNoBlobs()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Steel", 2)), Price = 5, GameTick = 1 }, n.Auth);

        var list = await n.Market.ListAsync(new MarketQuery(), n.Auth);

        Assert.Empty(Assert.Single(list.Lots).Items.Items[0].ScribeXmlZstd);
    }

    [Fact]
    public async Task BuyWithAFullMailboxChangesNothingAndSendsNoWalletEvent()
    {
        using var fixture = Server().WithSetting("Economy:MaxPendingParcels", "1");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await Fund(fixture, bob.Player.PlayerId, 1000);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth); // bob's mailbox is now full
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Gold", 2)), Price = 100, GameTick = 1 }, n.Auth);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), b.Auth);
        using var novaStream = events.Subscribe(new Empty(), n.Auth);

        await Assert.ThrowsAsync<RpcException>(() => b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth).ResponseAsync);
        fixture.Services.GetRequiredService<EventBus>().Broadcast(new ServerEvent { Notice = new Notice { Text = "fence" } });

        Assert.Equal(1000, (await b.Wallet.GetAsync(new Empty(), b.Auth)).Balance);
        Assert.Equal(0, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        Assert.Equal("fence", await FirstOf(bobStream, ServerEvent.BodyOneofCase.WalletChanged, ServerEvent.BodyOneofCase.Notice));
        Assert.Equal("fence", await FirstOf(novaStream, ServerEvent.BodyOneofCase.WalletChanged, ServerEvent.BodyOneofCase.Notice));
    }

    [Fact]
    public async Task ConcurrentWalletAdjustmentsKeepTheSum()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await fixture.RegisterAndLogin("nova");

        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var scope = fixture.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().AdjustWallet(actor, nova.Player.PlayerId, 10, "race");
        }));

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Equal(100, (await db.Wallets.SingleAsync(w => w.PlayerId == nova.Player.PlayerId)).Balance);
        Assert.Equal(10, await db.WalletEntries.CountAsync(e => e.PlayerId == nova.Player.PlayerId));
    }

    [Fact]
    public async Task RefundOfAPurchaseParcelIsRefused()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await Fund(fixture, bob.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Gold", 2)), Price = 100, GameTick = 1 }, n.Auth);
        var parcel = await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);

        using var scope = fixture.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AdminOperations>().RefundParcel(actor, parcel.Id));
        Assert.Equal(100, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        Assert.Single((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels);
    }

    [Fact]
    public async Task RefundOfAWithdrawParcelRestoresTheBalance()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await fixture.RegisterAndLogin("nova");
        await Fund(fixture, nova.Player.PlayerId, 200);
        var n = Clients(fixture, nova.SessionToken);
        var parcel = await n.Wallet.WithdrawAsync(new WithdrawRequest { ClientRequestId = "w1", Silver = 150 }, n.Auth);

        using var scope = fixture.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<AdminOperations>().RefundParcel(actor, parcel.Id));
        Assert.Equal(200, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        Assert.Empty((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
    }

    [Fact]
    public async Task RefundLotWorksWhileFrozen()
    {
        using var fixture = Server();
        var actor = await Admin(fixture);
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Gold", 2)), Price = 100, GameTick = 1 }, n.Auth);
        fixture.Services.GetRequiredService<FreezeState>().Frozen = true;

        using var scope = fixture.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<AdminOperations>().RefundLot(actor, lot.Id));
        Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
    }

    private static async Task<string> FirstOf(AsyncServerStreamingCall<ServerEvent> call, params ServerEvent.BodyOneofCase[] bodies)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(cts.Token))
        {
            var ev = call.ResponseStream.Current;
            if (bodies.Contains(ev.BodyCase)) return ev.BodyCase == ServerEvent.BodyOneofCase.Notice ? ev.Notice.Text : ev.BodyCase.ToString();
        }
        throw new TimeoutException();
    }
}
