using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;

namespace Rimlink.Server.Tests.Economy;

public class EconomyAdminTests
{
    private const string Token = "test-admin-token";

    private static ServerFixture Server() => EconomyServer().WithSetting("Admin:ApiToken", Token).WithSetting("Auth:AdminLogins:0", "admin");

    private static HttpClient Rest(ServerFixture fixture)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    [Fact]
    public async Task LotsCarrySellerNameAndSummary()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var n = Clients(fixture, nova.SessionToken);
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = Guid.NewGuid().ToString("N"), Items = Items(("Steel", 50), ("Component", 3)), Price = 300, GameTick = 100 }, n.Auth);

        var lots = await Rest(fixture).GetFromJsonAsync<System.Text.Json.JsonElement>("/api/admin/economy/lots");

        var row = lots.EnumerateArray().Single();
        Assert.Equal(lot.Id, row.GetProperty("id").GetInt64());
        Assert.Equal(nova.Player.PlayerId, row.GetProperty("sellerPlayerId").GetInt64());
        Assert.Equal("Nova", row.GetProperty("sellerName").GetString());
        Assert.Equal("Steel ×50, Component ×3", row.GetProperty("summary").GetString());
        Assert.Equal(300, row.GetProperty("price").GetInt64());
        Assert.Equal("Open", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WalletAdjustIsAppliedAndLogged()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);

        var response = await Rest(fixture).PostAsJsonAsync($"/api/admin/economy/wallet/{nova.Player.PlayerId}", new { delta = 500, reason = "compensation" });

        response.EnsureSuccessStatusCode();
        Assert.Equal(500, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        Assert.Contains(await db.AdminActions.ToListAsync(), a => a.Action == "wallet" && a.Details.Contains("compensation"));
        Assert.Contains(await db.WalletEntries.ToListAsync(), e => e.Kind == "admin" && e.Delta == 500);
    }

    [Fact]
    public async Task RefundParcelReturnsItToSender()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await Fund(fixture, nova.Player.PlayerId, 20);
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        var parcel = await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 4)), silver: 20), n.Auth);

        (await Rest(fixture).PostAsync($"/api/admin/economy/refund/parcel/{parcel.Id}", null)).EnsureSuccessStatusCode();

        Assert.Empty((await b.Mailbox.ListAsync(new Empty(), b.Auth)).Parcels);
        var back = Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels);
        Assert.Equal(ParcelKind.ParcelAdmin, back.Kind);
        Assert.Equal(4, back.Items.Items[0].Count);
        Assert.Equal(20, (await n.Wallet.GetAsync(new Empty(), n.Auth)).Balance);
    }

    [Fact]
    public async Task RefundLotWithdrawsIt()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Steel", 2)), Price = 5, GameTick = 1 }, n.Auth);

        (await Rest(fixture).PostAsync($"/api/admin/economy/refund/lot/{lot.Id}", null)).EnsureSuccessStatusCode();

        Assert.Empty((await n.Market.ListAsync(new MarketQuery(), n.Auth)).Lots);
        Assert.Equal(ParcelKind.ParcelLotReturn, Assert.Single((await n.Mailbox.ListAsync(new Empty(), n.Auth)).Parcels).Kind);
    }

    [Fact]
    public async Task FreezeViaChatCommandBlocksTrades()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());
        var n = Clients(fixture, nova.SessionToken);

        await chat.SendAsync(new SendRequest { Text = "/economy freeze maintenance window" }, Bearer(admin.SessionToken));
        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.EconomyFrozen, ex.ErrorDetail()?.Code);
        Assert.Contains("maintenance window", ex.Status.Detail);

        await chat.SendAsync(new SendRequest { Text = "/economy unfreeze" }, Bearer(admin.SessionToken));
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth);
    }

    [Fact]
    public async Task StatsReportCounts()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await Fund(fixture, nova.Player.PlayerId, 300);
        var n = Clients(fixture, nova.SessionToken);
        await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Steel", 2)), Price = 5, GameTick = 1 }, n.Auth);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1)), silver: 100), n.Auth);

        var stats = await Rest(fixture).GetFromJsonAsync<JsonElement>("/api/admin/economy/stats");

        Assert.Equal(200, stats.GetProperty("silverInWallets").GetInt64());
        Assert.Equal(1, stats.GetProperty("openLots").GetInt32());
        Assert.Equal(1, stats.GetProperty("pendingParcels").GetInt32());
        Assert.Equal(2, stats.GetProperty("tradesToday").GetInt32());
    }

    [Fact]
    public async Task ListsParcelsAndLotsOfAPlayer()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        await n.Transfer.SendAsync(Send(bob.Player.PlayerId, Items(("Steel", 1))), n.Auth);
        await n.Market.PostAsync(new PostRequest { ClientRequestId = "l1", Items = Items(("Steel", 2)), Price = 5, GameTick = 1 }, n.Auth);

        var parcels = await Rest(fixture).GetFromJsonAsync<JsonElement>($"/api/admin/economy/parcels/{bob.Player.PlayerId}");
        var lots = await Rest(fixture).GetFromJsonAsync<JsonElement>($"/api/admin/economy/lots?playerId={nova.Player.PlayerId}");

        Assert.Equal(1, parcels.GetArrayLength());
        Assert.Equal(1, lots.GetArrayLength());
    }
}
