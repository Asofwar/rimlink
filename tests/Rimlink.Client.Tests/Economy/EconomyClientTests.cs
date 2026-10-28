using System.Net.Http.Headers;
using System.Net.Http.Json;
using Grpc.Core;
using Rimlink.Client.Economy;
using Rimlink.Client.State;
using Rimlink.Client.Tests.Support;
using Rimlink.Protocol.V1;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Client.Tests.Economy;

public class EconomyClientTests
{
    private const string Token = "test-admin-token";

    private static ServerFixture Server() => EconomyServer().WithSetting("Admin:ApiToken", Token);

    [Fact]
    public async Task RecipientStateGetsTheParcelFromTheEventStream()
    {
        using var fixture = Server();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var state = new SharedState();
        bob.EventReceived += state.Apply;
        int arrived = 0;
        state.Economy.ParcelArrived += _ => arrived++;
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);

        var parcel = await n.Transfer.SendAsync(Send(bob.Login!.Player.PlayerId, Items(("Steel", 10))), n.Auth);

        await ClientFixtures.WaitUntil(() => state.Economy.Pending.Count == 1);
        Assert.Equal(parcel.Id, state.Economy.Pending[0].Id);
        state.Apply(new ServerEvent { ParcelArrived = new ParcelArrived { Parcel = state.Economy.Pending[0] } }); // replay
        Assert.Single(state.Economy.Pending);
        Assert.Equal(1, arrived);
    }

    [Fact]
    public async Task WalletChangedUpdatesBalance()
    {
        using var fixture = Server();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var state = new SharedState();
        bob.EventReceived += state.Apply;
        var rest = fixture.CreateClient();
        rest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        (await rest.PostAsJsonAsync($"/api/admin/economy/wallet/{bob.Login!.Player.PlayerId}", new { delta = 500, reason = "test" })).EnsureSuccessStatusCode();

        await ClientFixtures.WaitUntil(() => state.Economy.Balance == 500);
        Assert.Equal(500, (await new EconomyClient(bob).Wallet(CancellationToken.None)).Balance);
    }

    [Fact]
    public async Task SellerGetsLotSold()
    {
        using var fixture = Server();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var state = new SharedState();
        bob.EventReceived += state.Apply;
        var economy = new EconomyClient(bob);
        var lot = await economy.Post(new PostRequest { ClientRequestId = EconomyClient.NewRequestId(), Items = Items(("Steel", 2)), Price = 40, GameTick = 1 }, CancellationToken.None);
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Fund(fixture, nova.Player.PlayerId, 100);
        LotSold? sold = null;
        state.Economy.LotSold += s => sold = s;

        await Clients(fixture, nova.SessionToken).Market.BuyAsync(new LotRef { LotId = lot.Id }, Bearer(nova.SessionToken));

        await ClientFixtures.WaitUntil(() => sold is not null);
        Assert.Equal(lot.Id, sold!.LotId);
        Assert.Equal(40, sold.Price);
        Assert.Equal("Nova", sold.Buyer.Name);
        Assert.Equal(40, state.Economy.Balance);
    }

    [Fact]
    public async Task LoadFillsBalancePendingAndLots()
    {
        using var fixture = Server();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var economy = new EconomyClient(bob);
        await economy.Post(new PostRequest { ClientRequestId = EconomyClient.NewRequestId(), Items = Items(("Steel", 2)), Price = 40, GameTick = 1 }, CancellationToken.None);
        var nova = await fixture.RegisterAndLogin("nova");
        await Clients(fixture, nova.SessionToken).Transfer.SendAsync(Send(bob.Login!.Player.PlayerId, Items(("Gold", 1))), Bearer(nova.SessionToken));
        var state = new SharedState();

        state.Economy.Load(await economy.Wallet(CancellationToken.None), await economy.Pending(CancellationToken.None),
            await economy.Lots(new MarketQuery { SellerPlayerId = bob.Login.Player.PlayerId }, CancellationToken.None));

        Assert.Equal(0, state.Economy.Balance);
        Assert.Single(state.Economy.Pending);
        Assert.Single(state.Economy.MyLots);
        state.Economy.MarkDelivered(state.Economy.Pending[0].Id);
        Assert.Empty(state.Economy.Pending);
    }

    [Fact]
    public async Task CodeOfReadsTheErrorTrailer()
    {
        using var fixture = Server();
        await using var bob = await ClientFixtures.Connected(fixture, "bob");
        var economy = new EconomyClient(bob);

        var ex = await Assert.ThrowsAsync<RpcException>(() => economy.Send(Send(bob.Login!.Player.PlayerId, Items(("Steel", 1))), CancellationToken.None));

        Assert.Equal(ErrorCode.SelfTrade, EconomyClient.CodeOf(ex));
        Assert.Null(EconomyClient.CodeOf(new RpcException(new Status(StatusCode.Internal, "x"))));
    }
}
