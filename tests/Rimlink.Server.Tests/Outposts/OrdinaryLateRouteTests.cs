using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Raids;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_live_raid_and_derived_launch_share_the_same_terminal_decision(bool applied)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var original = LiveFixtures.Propose(2, LiveKind.LiveRaid, 1, Guid.NewGuid().ToString("N"), 1100);
        original.Pawns = new() { Pawns = { h.Blob.Clone() } };
        var departure = await h.Depart(original);
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var launch = OrdinaryCapture.DerivedLiveRaid(original);
        if (applied)
        {
            var raid = await raids.Launch(1, launch);
            await raids.Cancel(raid.Id, "finished before resolving original proposal");
            await OrdinarySourceAssertions.Applied(h, scope.ServiceProvider, original, raid.Id);
        }
        else
        {
            var cancel = CancelOriginal(h, original, departure);
            var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
            Assert.Equal(OrdinaryOperationStatus.ReturnStarted, (await returns.BeginReturnAsync(1, cancel, default)).Status);
            var error = await Assert.ThrowsAsync<RpcException>(() => raids.Launch(1, launch));
            Assert.Equal("ordinary-operation-return-fenced", error.Status.Detail);
            error = await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<LiveSessionService>().Propose(1, original));
            Assert.Equal("ordinary-operation-return-fenced", error.Status.Detail);
            Assert.Empty(await h.Db.Raids.ToListAsync()); Assert.Empty(await h.Db.LiveSessions.ToListAsync());
        }
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_report_cannot_evade_its_fence_by_dropping_all_local_captives(bool dropCaptives)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h);
        await h.Db.Players.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.FirstSaveAt, DateTime.UtcNow.AddDays(-30)).SetProperty(p => p.WealthTotal, 100000L));
        using var host = h.StartHost(allowKidnap: true); using var scope = host.Services.CreateScope();
        var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var launch = RaidFixtures.Launch(1, 1, PawnMode.PawnReal, tick: 1000);
        var raid = await raids.Launch(2, launch); await raids.Announce(1, raid.Id, 1000); await raids.Land(1, raid.Id, 1000);
        var original = new RaidReport { RaidId = raid.Id, GameTick = 1100, ClientRequestId = Guid.NewGuid().ToString("N"),
            Loot = new() { Items = { EconomyFixtures.Item("Steel", 13) } }, Captives = new() { Pawns = { h.Blob.Clone() } },
            Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeAlive, Blob = launch.Pawns.Pawns[0].Clone() } } };
        var departure = await h.Depart(original);
        var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, (await returns.BeginReturnAsync(1, CancelOriginal(h, original, departure), default)).Status);
        long parcels = await h.Db.Parcels.CountAsync();
        var late = original.Clone();
        if (dropCaptives) late.Captives = null; else late.Captives.Pawns[0].Registry = null;
        var error = await Assert.ThrowsAsync<RpcException>(() => raids.Report(1, late));
        Assert.Equal("ordinary-operation-return-fenced", error.Status.Detail);
        Assert.Equal(RaidStateDb.Landed, (await h.Db.Raids.AsNoTracking().SingleAsync()).State);
        Assert.Equal(parcels, await h.Db.Parcels.CountAsync());
        Assert.Empty(await h.Db.Transfers.Where(t => t.Kind == "raid-report").ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    private static OrdinaryCancelRequest CancelOriginal(CustodyHarness h, IMessage request, CustodyTransferEvidence departure) => new()
    {
        Scope = h.F.Scope.Clone(), Capture = departure.OrdinaryCapture.Clone(), OriginalRequest = request.ToByteString(),
        RemovalSnapshot = departure.RemovalSnapshot.Clone(), CanonicalSnapshot = departure.RemovalSnapshot.Clone(), ReturnAttemptId = Guid.NewGuid().ToString("D")
    };
}
