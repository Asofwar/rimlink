using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using Role = Rimlink.Server.Storage.Entities.Role;

namespace Rimlink.Server.Tests.Admin;

public class PlayerFreezeAdminTests
{
    private static ServerFixture Server() => EconomyServer().WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken);

    [Fact]
    public async Task RestFreezeAndUnfreeze()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var kai = await fixture.RegisterAndLogin("kai", playerName: "Kai");
        await Fund(fixture, nova.Player.PlayerId, 100);
        var n = Clients(fixture, nova.SessionToken);
        var rest = fixture.Rest();
        long id = nova.Player.PlayerId;

        (await rest.PostAsJsonAsync($"/api/admin/players/{id}/economy", new { frozen = true, reason = "dupe" })).EnsureSuccessStatusCode();
        var card = await rest.GetFromJsonAsync<JsonElement>($"/api/admin/players/{id}");
        var ex = await Assert.ThrowsAsync<RpcException>(() => n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10), n.Auth).ResponseAsync);

        Assert.NotEqual(JsonValueKind.Null, card.GetProperty("economyFrozenAt").ValueKind);
        Assert.Equal("dupe", card.GetProperty("economyFreezeReason").GetString());
        Assert.Equal(ErrorCode.PlayerFrozen, ex.ErrorDetail()?.Code);

        (await rest.PostAsJsonAsync($"/api/admin/players/{id}/economy", new { frozen = false })).EnsureSuccessStatusCode();
        card = await rest.GetFromJsonAsync<JsonElement>($"/api/admin/players/{id}");
        var actions = (await rest.GetFromJsonAsync<JsonElement>("/api/admin/actions")).EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();

        Assert.Equal(JsonValueKind.Null, card.GetProperty("economyFrozenAt").ValueKind);
        Assert.Contains("player-economy-freeze", actions);
        Assert.Contains("player-economy-unfreeze", actions);
        await n.Transfer.SendAsync(Send(kai.Player.PlayerId, silver: 10), n.Auth);
    }

    [Fact]
    public async Task ChatCommandsFreezeOnlyForAdmins()
    {
        using var fixture = Server();
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        using var scope = fixture.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<AdminCommandRunner>();
        var moderator = new Actor(99, 99, Role.Moderator, "mod");
        var admin = new Actor(98, 98, Role.Admin, "boss");

        await Assert.ThrowsAsync<RpcException>(() => runner.Execute(moderator, new AdminCommand { Name = "freeze", Args = { "Nova" } }));
        Assert.Equal("economy frozen for Nova", await runner.Execute(admin, new AdminCommand { Name = "freeze", Args = { "Nova", "dupe", "x" } }));
        var card = await scope.ServiceProvider.GetRequiredService<AdminDirectory>().Card(nova.Player.PlayerId);
        Assert.Equal("dupe x", card.EconomyFreezeReason);
        Assert.Equal("economy open for Nova", await runner.Execute(admin, new AdminCommand { Name = "unfreeze", Args = { "Nova" } }));
        Assert.Null((await scope.ServiceProvider.GetRequiredService<AdminDirectory>().Card(nova.Player.PlayerId)).EconomyFrozenAt);
    }
}
