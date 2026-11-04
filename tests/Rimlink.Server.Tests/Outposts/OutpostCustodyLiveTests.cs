using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Live;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostCustodyBridgeTests
{
    [Theory]
    [InlineData(LiveKind.LiveRaid)]
    [InlineData(LiveKind.LiveVisit)]
    public async Task Tagged_live_entry_acquires_authority_before_live_state_lock(LiveKind kind)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, kind, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var authority = await h.Authority.AcquireAsync([1, 2], default);
        var pending = service.Propose(1, request);
        var probing = scope.ServiceProvider.GetRequiredService<LiveStateLock>().Hold();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var probe = await probing.WaitAsync(deadline.Token);
            Assert.False(pending.IsCompleted); // The entry is waiting for A, not holding Live while it waits.
        }
        finally
        {
            await authority.DisposeAsync();
            try { await pending; } finally { (await probing).Dispose(); }
        }
        Assert.Equal("Escrow", (await h.Row()).LocationKind);
        var applied = await h.Db.LiveSessions.AsNoTracking().SingleAsync(s => s.GuestPlayerId == 1 && s.ClientRequestId == request.ClientRequestId);
        await OrdinarySourceAssertions.Applied(h, scope.ServiceProvider, request, applied.Id);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("guest-cancel")]
    [InlineData("decline")]
    [InlineData("expiry")]
    [InlineData("close-ending")]
    public async Task Tagged_live_visit_terminal_paths_return_one_receipt_owned_original(string ending)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, LiveKind.LiveVisit, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var session = await service.Propose(1, request);
        switch (ending)
        {
            case "guest-cancel": await service.Cancel(1, session.Id); break;
            case "decline": await service.Respond(2, session.Id, false); break;
            case "expiry": await service.ExpireDue(DateTime.UtcNow.AddDays(2)); break;
            default: await service.Respond(2, session.Id, true); await service.MarkEnding(session.Id, LiveEndReason.EndAdmin); await service.CloseEnding(session.Id); break;
        }
        var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind); Assert.Equal(1, row.PlayerId);
        var parcelId = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(SnapshotHarness.Custody, OutpostParcelPayload.Parser.ParseFrom((await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcelId)).Payload).PawnCustodyIds.Single());
        await service.ExpireDue(DateTime.UtcNow.AddDays(3)); await service.CloseEnding(session.Id);
        await OrdinarySourceAssertions.Applied(h, scope.ServiceProvider, request, session.Id);
        Assert.Single(await h.Db.OutpostParcelLinks.AsNoTracking().Where(p => p.ParcelId == parcelId).ToListAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Tagged_live_visit_report_keeps_combat_state_but_never_promotes_the_host_mirror()
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, LiveKind.LiveVisit, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var session = await service.Propose(1, request); await service.Respond(2, session.Id, true);
        var after = h.Blob.Clone(); after.Registry.NonAuthoritativeMirror = true; after.ScribeXmlZstd = ByteString.CopyFromUtf8("changed-in-visit");
        var report = new RaidReport { LiveSessionId = session.Id, ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1200,
            Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeAlive, Blob = after } } };
        await service.ReportVisit(2, report); await service.ReportVisit(2, report);
        var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind); Assert.Equal(1, row.PlayerId);
        long id = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
        var pawn = PawnList.Parser.ParseFrom((await h.Db.Parcels.AsNoTracking().SingleAsync(p => p.Id == id)).Pawns).Pawns.Single();
        Assert.Equal(SnapshotHarness.Custody, pawn.Registry.PawnCustodyId); Assert.False(pawn.Registry.NonAuthoritativeMirror);
        Assert.Equal(ByteString.CopyFromUtf8("changed-in-visit"), pawn.ScribeXmlZstd);
    }

    [Theory]
    [InlineData(PawnOutcome.OutcomeDead)]
    [InlineData(PawnOutcome.OutcomeCaptured)]
    public async Task Live_visit_dead_and_captured_outcomes_keep_lineage_until_exact_delivery_receipt(PawnOutcome outcome)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, LiveKind.LiveVisit, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var session = await service.Propose(1, request); await service.Respond(2, session.Id, true);
        var after = h.Blob.Clone(); after.Registry.NonAuthoritativeMirror = true; after.ScribeXmlZstd = ByteString.CopyFromUtf8("captured-visitor");
        await service.ReportVisit(2, new RaidReport { LiveSessionId = session.Id, ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1200,
            Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = outcome, Blob = outcome == PawnOutcome.OutcomeDead ? null : after } } });
        var row = await h.Row(); Assert.Equal(1, row.PlayerId); Assert.True(row.Tombstone);
        Assert.Equal(outcome == PawnOutcome.OutcomeDead ? "Dead" : "TransferPending", row.LocationKind);
        if (outcome == PawnOutcome.OutcomeCaptured)
        {
            long id = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
            var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(l => l.ParcelId == id); Assert.Equal(2, link.PlayerId);
            var baseline = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.PlayerId == 2 && v.IsCurrent);
            var target = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(baseline).Outposts.Metadata.Clone();
            var startRequest = new OutpostDeliveryRequest { Scope = target.Scope.Clone(), ParcelId = id, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest),
                ClientRequestId = Guid.NewGuid().ToString("N"), CanonicalSnapshot = new() { Scope = target.Scope.Clone(), SnapshotId = baseline.SnapshotId!, RawSha256 = ByteString.CopyFrom(baseline.RawSha256!) } };
            var start = await h.F.Parcels.BeginDeliveryAsync(2, startRequest, default);
            Assert.Equal(1, (await h.Row()).PlayerId);
            var attempt = await h.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync(a => a.Id == start.AttemptId);
            var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
            var registry = h.Blob.Registry.Clone(); registry.Scope = target.Scope.Clone(); registry.PawnReferenceToken = "Thing_CapturedVisitor";
            target.Registry.Add(registry);
            target.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
                TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = startRequest.ClientRequestId, IntentDigest = ByteString.CopyFrom(attempt.IntentDigest),
                Snapshot = startRequest.CanonicalSnapshot.Clone(), DeliveryAttemptId = start.AttemptId,
                PawnCustodyIds = { SnapshotHarness.Custody }, AssignmentIds = { SnapshotHarness.Assignment }, ParcelIds = { id } });
            target.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
            await h.F.F.H.Accept(h.F.F.H.Snapshot(target));
            Assert.Equal(2, (await h.Row()).PlayerId); Assert.Equal("Home", (await h.Row()).LocationKind);
            Assert.False((await h.Row()).Tombstone); Assert.True((await h.F.Parcels.BeginDeliveryAsync(2, startRequest, default)).AckOnly);
        }
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("assignment")]
    [InlineData("missing")]
    public async Task Live_visit_report_rejects_forged_or_stripped_host_mirror_before_effects(string fault)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, LiveKind.LiveVisit, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var session = await service.Propose(1, request); await service.Respond(2, session.Id, true);
        var after = h.Blob.Clone(); after.Registry.NonAuthoritativeMirror = true;
        if (fault == "uuid") after.Registry.PawnCustodyId = Guid.NewGuid().ToString("D");
        if (fault == "assignment") after.Registry.AssignmentId = Guid.NewGuid().ToString("D");
        if (fault == "missing") after.Registry = null;
        long before = await h.Db.Parcels.CountAsync();
        await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(() => service.ReportVisit(2, new RaidReport { LiveSessionId = session.Id,
            ClientRequestId = Guid.NewGuid().ToString("N"), GameTick = 1200, Outcomes = { new PawnOutcomeMsg { Index = 0, Outcome = PawnOutcome.OutcomeAlive, Blob = after } } }));
        Assert.Equal(before, await h.Db.Parcels.CountAsync()); Assert.Equal("Escrow", (await h.Row()).LocationKind);
        Assert.NotNull(await h.Db.LiveSessions.AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.Pawns).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tagged_live_raid_returns_use_the_parent_commit_without_nested_transaction(bool afterStart)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, LiveKind.LiveRaid, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var session = await service.Propose(1, request);
        if (afterStart) { await service.Respond(2, session.Id, true); await service.MarkEnding(session.Id, LiveEndReason.EndAdmin); await service.CloseEnding(session.Id); }
        else await service.Cancel(1, session.Id);
        var row = await h.Row(); Assert.Equal("ReturnPending", row.LocationKind);
        long id = long.Parse(row.LocationId, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Single(await h.Db.OutpostParcelLinks.AsNoTracking().Where(p => p.ParcelId == id).ToListAsync());
        Assert.Equal(Rimlink.Server.Storage.Entities.LiveStateDb.Ended, await h.Db.LiveSessions.AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.State).SingleAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_launch_and_return_roll_back_the_real_rows_with_their_custody_head(bool returning)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, LiveKind.LiveRaid, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        var session = returning ? await service.Propose(1, request) : null;
        var before = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        long parcelsBefore = await h.Db.Parcels.CountAsync();
        var authority = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>();
        authority.CommitCheckpoint = point => { if (point == "BeforePending") throw new IOException("live-before-pending"); };
        await Assert.ThrowsAsync<IOException>(async () => { if (returning) await service.Cancel(1, session!.Id); else await service.Propose(1, request); });
        authority.CommitCheckpoint = null;
        Assert.Equal(before.HeadId, (await h.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(parcelsBefore, await h.Db.Parcels.CountAsync());
        Assert.Equal(returning ? "Escrow" : "Home", (await h.Row()).LocationKind);
        Assert.Equal(returning ? 1 : 0, await h.Db.LiveSessions.CountAsync()); Assert.Equal(returning ? 1 : 0, await h.Db.Raids.CountAsync());
        if (returning)
        {
            Assert.Equal(Rimlink.Server.Storage.Entities.LiveStateDb.Proposed, (await h.Db.LiveSessions.AsNoTracking().SingleAsync()).State);
            await service.Cancel(1, session!.Id); Assert.Equal("ReturnPending", (await h.Row()).LocationKind);
        }
        else { await service.Propose(1, request); Assert.Equal("Escrow", (await h.Row()).LocationKind); }
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_cleanup_borrows_the_explicit_outer_lease_instead_of_waiting_for_itself(bool raid)
    {
        using var h = await CustodyHarness.Returned(); await PrepareRaidTarget(h); h.Sessions.ByPlayer(2)!.AtHome = true;
        var request = LiveFixtures.Propose(2, raid ? LiveKind.LiveRaid : LiveKind.LiveVisit, 1, Guid.NewGuid().ToString("N"), 1100); request.Pawns = new() { Pawns = { h.Blob.Clone() } };
        await h.Depart(request); using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        var live = scope.ServiceProvider.GetRequiredService<LiveSessionService>();
        await live.Propose(1, request);
        var scopedAuthority = scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Outposts.OutpostAuthority>();
        var held = await scopedAuthority.AcquireAsync([1, 2], default);
        await using var entry = scope.ServiceProvider.GetRequiredService<PawnCustodyBridge>().UseHeldEntry(held);
        Task pending = raid ? scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.Raids.RaidService>().CancelAllOf(1, "archive", true, entry)
            : live.EndAllOf(1, LiveEndReason.EndAdmin, entry);
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { await held.DisposeAsync(); await pending; }
        Assert.Equal("ReturnPending", (await h.Row()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }
}
