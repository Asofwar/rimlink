using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
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
    public async Task Actual_raid_launch_preserves_one_authority_for_real_and_snapshot(bool mirror)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h);
        var request = RaidFixtures.Launch(2, 1, mirror ? PawnMode.PawnSnapshot : PawnMode.PawnReal, requestId: Guid.NewGuid().ToString("N"));
        request.Pawns = new() { Pawns = { h.Blob.Clone() } }; request.Pawns.Pawns[0].Registry.NonAuthoritativeMirror = mirror;
        if (!mirror) await h.Depart(request);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var raid = await raids.Launch(1, request);
        var row = await h.Row(); Assert.Equal(1, row.PlayerId); Assert.Equal(SnapshotHarness.Custody, row.Id);
        Assert.Equal(mirror ? "Home" : "Escrow", row.LocationKind); Assert.Equal(!mirror, row.Tombstone);
        if (!mirror) Assert.Equal("raid:" + raid.Id, row.LocationId);
        Assert.Equal(raid.Id, (await raids.Launch(1, request)).Id);
        Assert.Single(await h.Db.OutpostTransitions.AsNoTracking().Where(t => t.Kind == "CustodyTransfer").ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_tagged_raid_cancel_and_expiry_return_original_uuid_once(bool expired)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h);
        var request = RaidFixtures.Launch(2, 1, PawnMode.PawnReal, requestId: Guid.NewGuid().ToString("N")); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var raid = await raids.Launch(1, request);
        if (expired) Assert.Equal(1, await raids.ExpireDue(DateTime.UtcNow.AddYears(1)));
        else Assert.True(await raids.Cancel(raid.Id, "test"));
        Assert.False(await raids.Cancel(raid.Id, "replay"));
        await OrdinarySourceAssertions.Applied(h, scope.ServiceProvider, request, raid.Id);
        var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind); Assert.Equal(1, row.PlayerId);
        long parcelId = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(l => l.ParcelId == parcelId);
        Assert.Equal(SnapshotHarness.Custody, OutpostParcelPayload.Parser.ParseFrom(link.Payload).PawnCustodyIds.Single());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(PawnOutcome.OutcomeAlive)]
    [InlineData(PawnOutcome.OutcomeDead)]
    [InlineData(PawnOutcome.OutcomeCaptured)]
    public async Task Tagged_raid_report_moves_only_the_index_bound_original(PawnOutcome outcome)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h);
        var launch = RaidFixtures.Launch(2, 1, PawnMode.PawnReal, requestId: Guid.NewGuid().ToString("N")); launch.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(launch);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var raid = await raids.Launch(1, launch); await raids.Announce(2, raid.Id, 1000); await raids.Land(2, raid.Id, 1000);
        var after = h.Blob.Clone(); after.ScribeXmlZstd = ByteString.CopyFromUtf8("changed-in-combat"); after.Registry.NonAuthoritativeMirror = true;
        var report = new RaidReport { RaidId = raid.Id, GameTick = 1100, ClientRequestId = Guid.NewGuid().ToString("N"),
            Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = outcome, Blob = outcome == PawnOutcome.OutcomeDead ? null : after } } };
        await raids.Report(2, report); Assert.Null(await raids.Report(2, report));
        var row = await h.Row(); Assert.Equal(1, row.PlayerId); Assert.Equal(SnapshotHarness.Custody, row.Id);
        Assert.Equal(outcome == PawnOutcome.OutcomeDead ? "Dead" : outcome == PawnOutcome.OutcomeCaptured ? "TransferPending" : "ReturnPending", row.LocationKind);
        Assert.True(row.Tombstone);
        if (outcome != PawnOutcome.OutcomeDead)
        {
            long parcelId = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
            var parcel = await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcelId);
            var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcelId);
            Assert.Equal(outcome == PawnOutcome.OutcomeCaptured ? 2 : 1, link.PlayerId);
            var pawn = PawnList.Parser.ParseFrom(parcel.Pawns).Pawns.Single();
            Assert.Equal(SnapshotHarness.Custody, pawn.Registry.PawnCustodyId); Assert.False(pawn.Registry.NonAuthoritativeMirror);
            Assert.Equal(ByteString.CopyFromUtf8("changed-in-combat"), pawn.ScribeXmlZstd);
        }
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("assignment")]
    [InlineData("missing")]
    public async Task Tagged_raid_report_rejects_forged_or_stripped_survivor_before_effects(string fault)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h);
        var launch = RaidFixtures.Launch(2, 1, PawnMode.PawnReal, requestId: Guid.NewGuid().ToString("N")); launch.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(launch);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope(); var raids = scope.ServiceProvider.GetRequiredService<RaidService>();
        var raid = await raids.Launch(1, launch); await raids.Announce(2, raid.Id, 1000); await raids.Land(2, raid.Id, 1000);
        var after = h.Blob.Clone(); after.Registry.NonAuthoritativeMirror = true;
        if (fault == "uuid") after.Registry.PawnCustodyId = Guid.NewGuid().ToString("D");
        if (fault == "assignment") after.Registry.AssignmentId = Guid.NewGuid().ToString("D");
        if (fault == "missing") after.Registry = null;
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => raids.Report(2, new RaidReport { RaidId = raid.Id, GameTick = 1100,
            ClientRequestId = Guid.NewGuid().ToString("N"), Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeAlive, Blob = after } } }));
        Assert.Equal(RaidStateDb.Landed, await h.Db.Raids.AsNoTracking().Where(x => x.Id == raid.Id).Select(x => x.State).SingleAsync());
        Assert.Empty(await h.Db.RaidOutcomes.ToListAsync()); Assert.Equal("Escrow", (await h.Row()).LocationKind);
    }

    [Fact]
    public async Task Legacy_cleanup_does_not_reacquire_its_callers_owner_and_save_locks()
    {
        using var h = new AuthorityHarness();
        var bridge = new PawnCustodyBridge(h.Db, h.Authority, new Rimlink.Server.Application.Sessions.SessionRegistry(), h.Files, h.Owners);
        using var owner = await h.Owners.Hold(1); using var save = await h.Files.PerStoreLock.HoldAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pawns = new PawnList { Pawns = { new PawnBlob { KindDef = "Colonist", ScribeXmlZstd = ByteString.CopyFromUtf8("legacy") } } };
        await using var transaction = await bridge.BeginSourceAsync(1, 1, pawns, CustodyTransferKind.CustodyTransferRaidReturn,
            1, "legacy", new byte[32], deadline.Token);
        Assert.False(transaction.HasEvidence); await transaction.CommitAsync(deadline.Token);
    }

    private static async Task PrepareRaidTarget(CustodyHarness h)
    {
        h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; metadata.Scope.OwnerPlayerId = 2;
        await h.F.F.H.Accept(h.F.F.H.Snapshot(metadata));
        await h.Db.Players.Where(p => p.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.FirstSaveAt, DateTime.UtcNow.AddDays(-30)).SetProperty(p => p.WealthTotal, 100000L));
    }
}
