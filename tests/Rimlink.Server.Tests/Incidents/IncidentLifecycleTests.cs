using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Incidents;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.IncidentFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Incidents;

/// <summary>Announce, fire, fail, expire and cancel (paid incidents spec §2, §3).</summary>
public class IncidentLifecycleTests
{
    private sealed record Pair(ServerFixture Fixture, LoginReply Nova, LoginReply Bob, Protocol.V1.Incidents.IncidentsClient Api)
    {
        public Metadata N => Bearer(Nova.SessionToken);
        public Metadata B => Bearer(Bob.SessionToken);
        public long NovaId => Nova.Player.PlayerId;
        public long BobId => Bob.Player.PlayerId;
    }

    private static async Task<Pair> NovaAndBob()
    {
        var fixture = IncidentServer().WithSetting("Raids:TargetCooldownHours", "0")
            .WithSetting("Admin:ApiToken", AdminRestExtensions.ApiToken).WithSetting("Auth:AdminLogins:0", "admin");
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        var bob = await fixture.RegisterAndLogin("bob", playerName: "Bob");
        await MakeTargetRaidable(fixture, bob.Player.PlayerId);
        await Fund(fixture, nova.Player.PlayerId, 10000);
        return new Pair(fixture, nova, bob, IncidentsOf(fixture));
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
    public async Task AnnounceSetsTheFireTick()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var two = await p.Api.OrderAsync(Order(p.BobId, level: 2), p.N);
        var three = await p.Api.OrderAsync(Order(p.BobId, level: 3), p.N);

        Assert.Equal(1000, (await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = two.Id, GameTick = 1000 }, p.B)).FireTick);
        var announced = await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = three.Id, GameTick = 1000 }, p.B);
        Assert.Equal((31000, IncidentState.IncidentAnnounced), (announced.FireTick, announced.State));
        // A second announce returns the same fire tick.
        Assert.Equal(31000, (await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = three.Id, GameTick = 5000 }, p.B)).FireTick);
    }

    [Fact]
    public async Task WeatherIsNotAnnouncedAhead()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId, entry: "cold-snap", level: 4), p.N);

        Assert.Equal(1000, (await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = order.Id, GameTick = 1000 }, p.B)).FireTick);
    }

    [Fact]
    public async Task ReportFiredFinishes()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        using var novaStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), p.N);
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);
        await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = order.Id, GameTick = 1000 }, p.B);

        await p.Api.ReportAsync(new IncidentReport { IncidentId = order.Id, Fired = true }, p.B);

        Assert.Equal(IncidentStateDb.Fired, await StateOf(fixture, order.Id));
        Assert.Empty((await p.Api.PendingAsync(new Empty(), p.B)).Incidents);
        var resolved = await ReadEvent(novaStream, ServerEvent.BodyOneofCase.IncidentResolved, e => e.IncidentResolved);
        Assert.Equal((IncidentState.IncidentFired, 0L), (resolved.Incident.State, resolved.Refund));
    }

    [Fact]
    public async Task ReportFailedRefunds()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);
        await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = order.Id, GameTick = 1000 }, p.B);

        await p.Api.ReportAsync(new IncidentReport { IncidentId = order.Id, Fired = false, Reason = "no spot" }, p.B);

        Assert.Equal(IncidentStateDb.Failed, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, p.NovaId));
        Assert.Equal(0, await BalanceOf(fixture, WalletService.BankPlayerId));
        Assert.Equal("no spot", Assert.Single((await p.Api.MineAsync(new Empty(), p.N)).Incidents).Reason);
    }

    [Fact]
    public async Task ReportTwiceIsANoOp()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);
        await p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = order.Id, GameTick = 1000 }, p.B);

        await p.Api.ReportAsync(new IncidentReport { IncidentId = order.Id, Fired = true }, p.B);
        await p.Api.ReportAsync(new IncidentReport { IncidentId = order.Id, Fired = false, Reason = "late" }, p.B);

        Assert.Equal(IncidentStateDb.Fired, await StateOf(fixture, order.Id));
        Assert.Equal(9900, await BalanceOf(fixture, p.NovaId));
    }

    [Fact]
    public async Task OnlyTheTargetAnnouncesAndReports()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);

        var ex = await Assert.ThrowsAsync<RpcException>(() => p.Api.AnnounceAsync(new AnnounceIncident { IncidentId = order.Id, GameTick = 1 }, p.N).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        ex = await Assert.ThrowsAsync<RpcException>(() => p.Api.ReportAsync(new IncidentReport { IncidentId = order.Id, Fired = false }, p.N).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ExpiryRefunds()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().IncidentOrders.Where(i => i.Id == order.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IncidentService>().ExpireDue(DateTime.UtcNow));

        Assert.Equal(IncidentStateDb.Expired, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, p.NovaId));
    }

    [Fact]
    public async Task AdminCancelRefunds()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);
        using var scope = fixture.Services.CreateScope();

        Assert.True(await scope.ServiceProvider.GetRequiredService<IncidentService>().Cancel(order.Id, "abuse"));
        Assert.False(await scope.ServiceProvider.GetRequiredService<IncidentService>().Cancel(order.Id, "again"));

        Assert.Equal(IncidentStateDb.Cancelled, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, p.NovaId));
    }

    [Fact]
    public async Task CancelAndExpiryTellTheTarget()
    {
        // The target's client drops the incident from its queue (and stops closing its outflow), as RaidExpired does for raids.
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        using var bobStream = new Protocol.V1.Events.EventsClient(fixture.CreateChannel()).Subscribe(new Empty(), p.B);
        var cancelled = await p.Api.OrderAsync(Order(p.BobId), p.N);
        var expired = await p.Api.OrderAsync(Order(p.BobId, entry: "cold-snap"), p.N);
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IncidentService>();
        await service.Cancel(cancelled.Id, "abuse");
        await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().IncidentOrders.Where(i => i.Id == expired.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        await service.ExpireDue(DateTime.UtcNow);

        var first = await ReadEvent(bobStream, ServerEvent.BodyOneofCase.IncidentResolved, e => e.IncidentResolved);
        var second = await ReadEvent(bobStream, ServerEvent.BodyOneofCase.IncidentResolved, e => e.IncidentResolved);
        Assert.Equal((cancelled.Id, IncidentState.IncidentCancelled, 0L), (first.Incident.Id, first.Incident.State, first.Refund));
        Assert.Equal((expired.Id, IncidentState.IncidentExpired, 0L), (second.Incident.Id, second.Incident.State, second.Refund));
    }

    [Fact]
    public async Task PermanentBanCancelsAndRefunds()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);

        (await fixture.Rest().PostAsJsonAsync("/api/admin/bans", new { playerId = p.BobId, reason = "cheating" })).EnsureSuccessStatusCode();

        Assert.Equal(IncidentStateDb.Cancelled, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, p.NovaId));
    }

    [Fact]
    public async Task ATemporaryBanLeavesTheOrderWaiting()
    {
        // The player comes back; the order runs then or expires as usual.
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);

        (await fixture.Rest().PostAsJsonAsync("/api/admin/bans", new { playerId = p.BobId, reason = "cool down", days = 1.0 })).EnsureSuccessStatusCode();

        Assert.Equal(IncidentStateDb.Queued, await StateOf(fixture, order.Id));
    }

    [Fact]
    public async Task ArchivingTheTargetCancelsAndRefunds()
    {
        using var fixture = HygieneFixtures.HygieneServer();
        var bob = await HygieneFixtures.Settled(fixture, "bob", tile: 100);
        var nova = await fixture.RegisterAndLogin("nova", playerName: "Nova");
        await Fund(fixture, nova.Player.PlayerId, 10000);
        var order = await IncidentsOf(fixture).OrderAsync(Order(bob.PlayerId, entry: "trader"), Bearer(nova.SessionToken));
        var sessions = fixture.Services.GetRequiredService<SessionRegistry>();
        sessions.End(sessions.ByPlayer(bob.PlayerId)!.Token);

        (await fixture.Rest().PostAsync($"/api/admin/players/{bob.PlayerId}/archive", null)).EnsureSuccessStatusCode();

        Assert.Equal(IncidentStateDb.Cancelled, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, nova.Player.PlayerId));
    }

    [Fact]
    public async Task ResetCancelsAndRefunds()
    {
        var p = await NovaAndBob();
        using var fixture = p.Fixture;
        var order = await p.Api.OrderAsync(Order(p.BobId), p.N);

        (await fixture.Rest().PostAsync($"/api/admin/players/{p.BobId}/reset", null)).EnsureSuccessStatusCode();

        Assert.Equal(IncidentStateDb.Cancelled, await StateOf(fixture, order.Id));
        Assert.Equal(10000, await BalanceOf(fixture, p.NovaId));
    }
}
