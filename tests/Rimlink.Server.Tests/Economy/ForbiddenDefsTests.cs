using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Economy;

/// <summary>The admin forbids defs in sends and lots (economy hardening spec §5).</summary>
public class ForbiddenDefsTests
{
    private static ServerFixture Server(ServerFixture? fixture = null) => (fixture ?? RaidServer())
        .WithSetting("Economy:SendCooldownSeconds", "0").WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithSetting("Auth:AdminLogins:0", "admin");

    private static async Task Forbid(ServerFixture fixture, params string[] defs) =>
        (await fixture.Rest().PutAsJsonAsync("/api/admin/economy/forbidden-defs", new { defs })).EnsureSuccessStatusCode();

    private static ItemBlob ItemOf(string def, string stuff = "")
    {
        var item = Item(def, 1);
        item.StuffDef = stuff;
        return item;
    }

    private static async Task AssertForbidden(Func<Task> call, string def)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(ErrorCode.DefForbidden, ex.ErrorDetail()!.Code);
        Assert.Equal($"def '{def}' is forbidden for transfers", ex.Status.Detail);
    }

    [Fact]
    public async Task ForbiddenItemStuffAndKindAreRefusedForSendAndPost()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        await Forbid(fixture, "Gun_Revolver", "Leather_Thrumbo", "Thrumbo");
        long to = bob.Player.PlayerId;

        await AssertForbidden(() => n.Transfer.SendAsync(Send(to, items: new ItemList { Items = { ItemOf("Gun_Revolver") } }), n.Auth).ResponseAsync, "Gun_Revolver");
        await AssertForbidden(() => n.Transfer.SendAsync(Send(to, items: new ItemList { Items = { ItemOf("Apparel_Parka", "Leather_Thrumbo") } }), n.Auth).ResponseAsync, "Leather_Thrumbo");
        await AssertForbidden(() => n.Transfer.SendAsync(Send(to, pawns: Pawns(("Thrumbo", "Big"))), n.Auth).ResponseAsync, "Thrumbo");
        await AssertForbidden(() => n.Market.PostAsync(new PostRequest { ClientRequestId = "p1", Items = new ItemList { Items = { ItemOf("Gun_Revolver") } }, Price = 10, GameTick = 100 }, n.Auth).ResponseAsync, "Gun_Revolver");
        await n.Transfer.SendAsync(Send(to, items: Items(("Steel", 5))), n.Auth);
    }

    [Fact]
    public async Task RaidsIgnoreTheList()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        await Forbid(fixture, "Colonist");
        var n = RaidClientsOf(fixture, nova.SessionToken);

        var raid = await n.Raids.LaunchAsync(Launch(bob.Player.PlayerId), n.Auth);

        Assert.True(raid.Id > 0);
    }

    [Fact]
    public async Task OpenLotsStayOpen()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var bob = await fixture.RegisterAndLogin("bob");
        var n = Clients(fixture, nova.SessionToken);
        var b = Clients(fixture, bob.SessionToken);
        await Fund(fixture, bob.Player.PlayerId, 100);
        var lot = await n.Market.PostAsync(new PostRequest { ClientRequestId = "p1", Items = Items(("Gun_Revolver", 1)), Price = 10, GameTick = 100 }, n.Auth);

        await Forbid(fixture, "Gun_Revolver");

        await b.Market.BuyAsync(new LotRef { LotId = lot.Id }, b.Auth);
    }

    [Fact]
    public async Task AdminSetsListsAndAuditsTheList()
    {
        using var fixture = Server();

        await Forbid(fixture, "Thrumbo", " Gun_Revolver ", "", "Thrumbo");
        var listed = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/economy/forbidden-defs");

        Assert.Equal(["Gun_Revolver", "Thrumbo"], listed.GetProperty("defs").EnumerateArray().Select(e => e.GetString()!).ToArray());
        using (var scope = fixture.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().AdminActions.AnyAsync(a => a.Action == "forbidden-defs"));
        var refused = await fixture.Rest("not-the-token").PutAsJsonAsync("/api/admin/economy/forbidden-defs", new { defs = new[] { "Steel" } });
        Assert.False(refused.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ChatCommandEditsTheList()
    {
        using var fixture = Server();
        using var scope = fixture.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AdminCommandRunner>();
        Task<string> Run(params string[] args) => runner.Execute(Actor.Api, new AdminCommand { Name = "forbid", Args = { args } });

        await Run("add", "A", "B");
        await Run("remove", "A");

        Assert.Equal("B", await Run("list"));
    }

    [Fact]
    public async Task RulesCarryTheListAndTheFees()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova");
        var n = Clients(fixture, nova.SessionToken);
        await Forbid(fixture, "Thrumbo", "Gun_Revolver");

        var rules = await n.Wallet.RulesAsync(new Empty(), n.Auth);

        Assert.Equal(["Gun_Revolver", "Thrumbo"], rules.ForbiddenDefs.ToArray());
        Assert.Equal(0.5, rules.StorageFeePercentPerDay);
        Assert.Equal(3, rules.StorageFreeDays);
    }

    [Fact]
    public async Task TheListSurvivesARestart()
    {
        string dataDir;
        using (var fixture = Server().KeepDataDir())
        {
            dataDir = fixture.DataDir;
            await Forbid(fixture, "Gun_Revolver");
        }
        using var again = Server(RaidServer().WithDataDir(dataDir));
        var nova = await again.RegisterAndLogin("nova");
        var n = Clients(again, nova.SessionToken);

        Assert.Equal(["Gun_Revolver"], (await n.Wallet.RulesAsync(new Empty(), n.Auth)).ForbiddenDefs.ToArray());
    }
}
