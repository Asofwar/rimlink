using System.Net.Http.Headers;
using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Storage;
using LiveSessionRow = Rimlink.Server.Storage.Entities.LiveSession;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;
using static Rimlink.Server.Tests.Support.EconomyFixtures;
using static Rimlink.Server.Tests.Support.GrpcTestExtensions;
using static Rimlink.Server.Tests.Support.LiveFixtures;
using static Rimlink.Server.Tests.Support.RaidFixtures;

namespace Rimlink.Server.Tests.Live;

/// <summary>Spec §2 Ending/Ended: the host's report turns the guest's pawns into parcels; a lost host reports at the next login.</summary>
public class LiveEndTests
{
    private sealed record Pair(ServerFixture Fixture, LiveClients Nova, LiveClients Bob);

    private static async Task<Pair> Started(LiveKind kind, ServerFixture? fixture = null)
    {
        fixture ??= LiveServer();
        var nova = await Connected(fixture, "nova");
        var bob = await Connected(fixture, "bob");
        await MakeTargetRaidable(fixture, bob.PlayerId);
        await bob.Live.SetAutoAcceptAsync(new AutoAcceptRequest { Enabled = true }, bob.Auth);
        return new Pair(fixture, nova, bob);
    }

    private static async Task<LiveSessionRow> Row(ServerFixture fixture, long id)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().LiveSessions.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private static async Task Service(ServerFixture fixture, Func<LiveSessionService, Task> action)
    {
        using var scope = fixture.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<LiveSessionService>());
    }

    [Fact]
    public async Task ReportForALiveRaidEndsTheSessionAndParcelsTheGuest()
    {
        var p = await Started(LiveKind.LiveRaid);
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);
        await Service(fixture, s => s.MarkRunning(session.Id));

        var report = AllAlive(session.RaidId, session.Id, pawns: 2);
        report.Outcomes[1].Outcome = PawnOutcome.OutcomeDead;
        report.Outcomes[1].Blob = null;
        report.Loot.Items.Add(Item("Steel", 10, marketValueX100: 190));
        await p.Bob.Raids.ReportAsync(report, p.Bob.Auth);

        var parcels = (await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels;
        Assert.Equal(2, parcels.Count);
        Assert.Equal(1, Assert.Single(parcels, x => x.Kind == ParcelKind.ParcelRaidReturn).Pawns.Pawns.Count);
        Assert.Single(parcels, x => x.Kind == ParcelKind.ParcelLoot);
        Assert.Equal(LiveStateDb.Ended, (await Row(fixture, session.Id)).State);
        var ended = await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded);
        Assert.Equal(LiveState.LiveEnded, ended.Session.State);
        Assert.Equal(RaidState.RaidResolved, Assert.Single((await p.Nova.Raids.MineAsync(new Empty(), p.Nova.Auth)).Raids).State);
        var again = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth); // both are free again
        Assert.Equal(LiveState.LiveStarting, again.State);
    }

    [Fact]
    public async Task VisitReportReturnsAllLivingPawnsAndIgnoresLoot()
    {
        var p = await Started(LiveKind.LiveVisit);
        using var fixture = p.Fixture;
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit, pawns: 3), p.Nova.Auth);
        await Service(fixture, s => s.MarkRunning(session.Id));

        var report = AllAlive(0, session.Id, pawns: 3, tick: 900);
        report.Outcomes[2].Outcome = PawnOutcome.OutcomeFled;
        report.Loot.Items.Add(Item("Gold", 5, marketValueX100: 10000)); // the host's things never travel with a visitor
        await p.Bob.Raids.ReportAsync(report, p.Bob.Auth);

        var parcel = Assert.Single((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels);
        Assert.Equal(ParcelKind.ParcelRaidReturn, parcel.Kind);
        Assert.Equal(3, parcel.Pawns.Pawns.Count);
        Assert.Equal(LiveStateDb.Ended, (await Row(fixture, session.Id)).State);
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(900, await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().Players.Where(x => x.Id == p.Bob.PlayerId).Select(x => x.LastOutgoingTick).SingleAsync());
    }

    [Fact]
    public async Task HostLostEndsTheSessionAndPendsTheEndForTheHost()
    {
        var p = await Started(LiveKind.LiveRaid);
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);
        await Service(fixture, s => s.MarkRunning(session.Id));

        await Service(fixture, s => s.MarkEnding(session.Id, LiveEndReason.EndHostLost));

        var ended = await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded);
        Assert.Equal(LiveEndReason.EndHostLost, ended.Session.EndReason);
        Assert.Empty((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels);

        var bobAgain = await fixture.RegisterAndLogin("bob");
        Assert.Equal(session.Id, Assert.Single(bobAgain.PendingLiveEnds).Id);
        var b = LiveClientsOf(fixture, bobAgain);
        await b.Raids.ReportAsync(AllAlive(session.RaidId, session.Id, pawns: 2), b.Auth);

        Assert.Equal(ParcelKind.ParcelRaidReturn, Assert.Single((await p.Nova.Mailbox.ListAsync(new Empty(), p.Nova.Auth)).Parcels).Kind);
        Assert.Empty((await fixture.RegisterAndLogin("bob")).PendingLiveEnds);
    }

    [Fact]
    public async Task WaitingExpiresToEnding()
    {
        var p = await Started(LiveKind.LiveVisit, LiveServer().WithSetting("Live:ReconnectWaitMinutes", "0"));
        using var fixture = p.Fixture;
        using var bobEvents = p.Bob.Subscribe();
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveVisit), p.Nova.Auth);
        await Service(fixture, s => s.MarkRunning(session.Id));

        await Service(fixture, s => s.MarkWaiting(session.Id, p.Nova.PlayerId));
        Assert.Equal(session.Id, (await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.LivePeerLost, e => e.LivePeerLost)).SessionId);

        var ended = await ReadEvent(bobEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded, TimeSpan.FromSeconds(10));
        Assert.Equal(LiveEndReason.EndGuestLost, ended.Session.EndReason);
        Assert.Equal(LiveStateDb.Ending, (await Row(fixture, session.Id)).State);
    }

    [Fact]
    public async Task AdminCanListAndEndASession()
    {
        const string token = "test-admin-token";
        var p = await Started(LiveKind.LiveRaid, LiveServer().WithSetting("Admin:ApiToken", token));
        using var fixture = p.Fixture;
        using var novaEvents = p.Nova.Subscribe();
        var session = await p.Nova.Live.ProposeAsync(Propose(p.Bob.PlayerId, LiveKind.LiveRaid), p.Nova.Auth);
        var rest = fixture.CreateClient();
        rest.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var list = await rest.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/admin/live");
        Assert.Equal(session.Id, Assert.Single(list!).GetProperty("id").GetInt64());
        (await rest.PostAsJsonAsync($"/api/admin/live/{session.Id}/end", new { reason = "stuck" })).EnsureSuccessStatusCode();

        Assert.Equal(LiveEndReason.EndAdmin, (await ReadEvent(novaEvents, ServerEvent.BodyOneofCase.LiveEnded, e => e.LiveEnded)).Session.EndReason);
        using var scope = fixture.Services.CreateScope();
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<RimlinkDbContext>().AdminActions.ToListAsync(), a => a.Action == "live-end" && a.Details.Contains("stuck"));
    }
}
