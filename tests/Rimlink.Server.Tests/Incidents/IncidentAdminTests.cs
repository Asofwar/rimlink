using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.IncidentFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Incidents;

/// <summary>Admins list and cancel paid incidents (paid incidents spec §5).</summary>
public class IncidentAdminTests
{
    private const string ReadToken = "read-token-0123456789abcdef";

    private static ServerFixture Server() => IncidentServer().WithSetting("Raids:TargetCooldownHours", "0")
        .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithSetting("Auth:AdminLogins:0", "admin")
        .WithToken(0, "reader", ReadToken, "read");

    private static async Task<(LoginReply Nova, LoginReply Bob, Incident Order)> NovaOrdersAgainstBob(ServerFixture fixture)
    {
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        await Fund(fixture, nova.Player.PlayerId, 10000);
        var order = await IncidentsOf(fixture).OrderAsync(Order(bob.Player.PlayerId), Bearer(nova.SessionToken));
        return (nova, bob, order);
    }

    private static async Task<long> BalanceOf(ServerFixture fixture, long playerId)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WalletService>().Balance(playerId);
    }

    private static async Task<IncidentStateDb> StateOf(ServerFixture fixture, long id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().IncidentOrders.AsNoTracking().SingleAsync(i => i.Id == id)).State;
    }

    [Fact]
    public async Task RestListsAndCancels()
    {
        using var fixture = Server();
        var (nova, bob, order) = await NovaOrdersAgainstBob(fixture);

        var list = await fixture.Rest().GetFromJsonAsync<JsonElement>($"/api/admin/incidents?playerId={bob.Player.PlayerId}");
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(order.Id, row.GetProperty("id").GetInt64());
        Assert.Equal(("Nova", "Bob", "pirates", "Queued"),
            (row.GetProperty("buyerName").GetString(), row.GetProperty("targetName").GetString(), row.GetProperty("entryId").GetString(), row.GetProperty("state").GetString()));
        Assert.Equal((2, false, order.Price), (row.GetProperty("level").GetInt32(), row.GetProperty("pods").GetBoolean(), row.GetProperty("price").GetInt64()));

        // A read token sees the list but cannot cancel; a player's session token is not an admin token.
        Assert.Equal(HttpStatusCode.OK, (await fixture.Rest(ReadToken).GetAsync("/api/admin/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Rest(ReadToken).PostAsJsonAsync($"/api/admin/incidents/{order.Id}/cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Rest(nova.SessionToken).GetAsync("/api/admin/incidents")).StatusCode);
        Assert.Equal(IncidentStateDb.Queued, await StateOf(fixture, order.Id));

        (await fixture.Rest().PostAsJsonAsync($"/api/admin/incidents/{order.Id}/cancel", new { reason = "abuse" })).EnsureSuccessStatusCode();

        Assert.Equal(IncidentStateDb.Cancelled, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, nova.Player.PlayerId));
        using (var scope = fixture.Services.CreateScope())
            Assert.Contains(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().AdminActions.ToListAsync(),
                a => a.Action == "incident-cancel" && a.Details.Contains("abuse") && a.Details.Contains(order.Id.ToString()));
        var after = await fixture.Rest().GetFromJsonAsync<JsonElement>("/api/admin/incidents");
        Assert.Equal(("Cancelled", "abuse"), (after[0].GetProperty("state").GetString(), after[0].GetProperty("reason").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Rest().PostAsJsonAsync($"/api/admin/incidents/{order.Id}/cancel", new { reason = "again" })).StatusCode);
    }

    [Fact]
    public async Task ChatListsAndCancels()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var (nova, _, order) = await NovaOrdersAgainstBob(fixture);
        var api = new Protocol.V1.Admin.AdminClient(fixture.CreateChannel());

        var list = await api.ExecuteAsync(Cmd("incidents", "list", "Bob"), Bearer(admin.SessionToken));
        Assert.Contains($"#{order.Id}", list.Text);
        Assert.Contains("pirates", list.Text);
        Assert.Contains("Queued", list.Text);
        Assert.Equal("no incidents", (await api.ExecuteAsync(Cmd("incidents", "list", "admin"), Bearer(admin.SessionToken))).Text);

        var cancel = await api.ExecuteAsync(Cmd("incidents", "cancel", order.Id.ToString(), "too", "much"), Bearer(admin.SessionToken));

        Assert.Equal("incident cancelled", cancel.Text);
        Assert.Equal(IncidentStateDb.Cancelled, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, nova.Player.PlayerId));
        Assert.Equal("no such waiting incident", (await api.ExecuteAsync(Cmd("incidents", "cancel", order.Id.ToString()), Bearer(admin.SessionToken))).Text);
    }

    private static AdminCommand Cmd(string name, params string[] args)
    {
        var c = new AdminCommand { Name = name };
        c.Args.AddRange(args);
        return c;
    }
}
