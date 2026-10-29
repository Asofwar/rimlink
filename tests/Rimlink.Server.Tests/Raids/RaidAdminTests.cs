using System.Net;
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
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Raids;

public class RaidAdminTests
{
    private const string Token = "test-admin-token";

    private static ServerFixture Server() => RaidServer().WithSetting("Admin:ApiToken", Token).WithSetting("Auth:AdminLogins:0", "admin");

    private static HttpClient Rest(ServerFixture fixture)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private sealed record Pair(LoginReply Nova, LoginReply Bob, RaidClients N, RaidClients B);

    private static async Task<Pair> NovaAndBob(ServerFixture fixture)
    {
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        return new Pair(nova, bob, RaidClientsOf(fixture, nova.SessionToken), RaidClientsOf(fixture, bob.SessionToken));
    }

    [Fact]
    public async Task CancelledQueuedRaidTellsTheTarget()
    {
        using var fixture = Server();
        var p = await NovaAndBob(fixture);
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        var events = new Protocol.V1.Events.EventsClient(fixture.CreateChannel());
        using var bobStream = events.Subscribe(new Empty(), p.B.Auth);

        (await Rest(fixture).PostAsJsonAsync($"/api/admin/raids/{raid.Id}/cancel", new { reason = "abuse" })).EnsureSuccessStatusCode();

        // The target's client stops treating the raid as incoming (and stops blocking its economy).
        Assert.Equal(raid.Id, await EconomyFixtures.ReadEvent(bobStream, ServerEvent.BodyOneofCase.RaidExpired, e => e.RaidExpired.RaidId));
    }

    [Fact]
    public async Task CancelReturnsThePawnsAndAudits()
    {
        using var fixture = Server();
        var p = await NovaAndBob(fixture);
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);

        (await Rest(fixture).PostAsJsonAsync($"/api/admin/raids/{raid.Id}/cancel", new { reason = "abuse" })).EnsureSuccessStatusCode();

        Assert.Equal(RaidState.RaidCancelled, Assert.Single((await p.N.Raids.MineAsync(new Empty(), p.N.Auth)).Raids).State);
        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.N.Mailbox.ListAsync(new Empty(), p.N.Auth)).Parcels).Kind);
        Assert.Empty((await p.B.Raids.PendingAsync(new Empty(), p.B.Auth)).Raids);
        using var scope = fixture.Services.CreateScope();
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().AdminActions.ToListAsync(), a => a.Action == "raid-cancel" && a.Details.Contains("abuse"));
        Assert.Equal(HttpStatusCode.NotFound, (await Rest(fixture).PostAsJsonAsync($"/api/admin/raids/{raid.Id}/cancel", new { reason = "again" })).StatusCode);
    }

    [Fact]
    public async Task FreezeRefusesLaunchWithEconomyFrozen()
    {
        using var fixture = Server();
        var admin = await fixture.RegisterAndLogin("admin");
        var p = await NovaAndBob(fixture);
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        await chat.SendAsync(new SendRequest { Text = "/raids freeze tournament pause" }, Bearer(admin.SessionToken));
        var ex = await Assert.ThrowsAsync<RpcException>(() => p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth).ResponseAsync);
        Assert.Equal(ErrorCode.EconomyFrozen, ex.ErrorDetail()!.Code);
        Assert.Contains("tournament pause", ex.Status.Detail);

        await chat.SendAsync(new SendRequest { Text = "/raids unfreeze" }, Bearer(admin.SessionToken));
        await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
    }

    [Fact]
    public async Task StatsCountStates()
    {
        using var fixture = Server();
        var p = await NovaAndBob(fixture);
        var raid = await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
        await p.B.Raids.AnnounceAsync(new RaidTick { RaidId = raid.Id, GameTick = 1 }, p.B.Auth);

        var stats = await Rest(fixture).GetFromJsonAsync<JsonElement>("/api/admin/raids/stats");
        var list = await Rest(fixture).GetFromJsonAsync<JsonElement>($"/api/admin/raids?playerId={p.Bob.Player.PlayerId}");

        Assert.Equal(0, stats.GetProperty("queued").GetInt32());
        Assert.Equal(1, stats.GetProperty("announced").GetInt32());
        Assert.Equal(0, stats.GetProperty("landed").GetInt32());
        Assert.False(stats.GetProperty("frozen").GetBoolean());
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public async Task AdminPeacefulOffHasNoDelay()
    {
        using var fixture = Server();
        var p = await NovaAndBob(fixture);
        await p.B.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = true }, p.B.Auth);
        await p.B.Raids.SetPeacefulAsync(new PeacefulRequest { Peaceful = false }, p.B.Auth);
        Assert.True((await p.B.Raids.FlagsAsync(new Empty(), p.B.Auth)).Peaceful);

        (await Rest(fixture).PostAsJsonAsync($"/api/admin/players/{p.Bob.Player.PlayerId}/peaceful", new { peaceful = false })).EnsureSuccessStatusCode();

        Assert.False((await p.B.Raids.FlagsAsync(new Empty(), p.B.Auth)).Peaceful);
        await p.N.Raids.LaunchAsync(Launch(p.Bob.Player.PlayerId), p.N.Auth);
    }

    [Fact]
    public async Task RestEndpointsNeedTheToken()
    {
        using var fixture = Server();

        var response = await fixture.CreateClient().GetAsync("/api/admin/raids/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChatCommandsNeedAdmin()
    {
        using var fixture = Server();
        var p = await NovaAndBob(fixture);
        var chat = new Protocol.V1.Chat.ChatClient(fixture.CreateChannel());

        var ex = await Assert.ThrowsAsync<RpcException>(() => chat.SendAsync(new SendRequest { Text = "/raids stats" }, Bearer(p.Nova.SessionToken)).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }
}
