using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Grpc;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostMutationTests
{
    [Fact]
    public async Task Claim_concurrent_with_close_cannot_duplicate_stock()
    {
        using var h = await MutationHarness.Create(); h.F.Time.Advance(TimeSpan.FromHours(1));
        var claim = h.Request(OutpostMutationKind.Claim); var close = h.Request(OutpostMutationKind.Close);
        async Task<OutpostOperationReply?> Attempt(OutpostMutationRequest r)
        { try { return await h.Service.MutateAsync(1, r, default); } catch (RpcException) { return null; } }
        var replies = await Task.WhenAll(Attempt(claim), Attempt(close));
        Assert.Single(replies, r => r is not null);
        Assert.Equal(20, await h.ResourceCount());
        Assert.Equal(OutpostStartupState.Ready, (await h.F.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Partial_recall_settles_old_roster()
    {
        using var h = await MutationHarness.Create(2); h.F.Time.Advance(TimeSpan.FromHours(1));
        var recall = h.Request(OutpostMutationKind.Recall); recall.PawnCustodyIds.Add(h.Workers[0].PawnCustodyId);
        recall.AssignmentIds.Add(h.Workers[0].AssignmentId); MutationHarness.Digest(recall);
        await h.Service.MutateAsync(1, recall, default);
        Assert.Equal(40, (await h.Row()).Stock); Assert.Single(await h.Db.OutpostAssignments.Where(a => a.Active).ToListAsync());
        h.F.Time.Advance(TimeSpan.FromHours(1));
        var state = await h.Service.StateAsync(1, new() { Scope = h.Scope.Clone() }, default);
        Assert.Equal(60, Assert.Single(state.Outposts).Stock);
        Assert.Equal(1, await h.Db.PawnCustody.CountAsync(p => p.LocationKind == "ReturnPending"));
    }

    [Fact]
    public async Task Last_recall_closes_and_returns_all_integer_stock()
    {
        using var h = await MutationHarness.Create(); h.F.Time.Advance(TimeSpan.FromMinutes(31));
        var recall = h.Request(OutpostMutationKind.Recall); recall.PawnCustodyIds.Add(h.Workers[0].PawnCustodyId);
        recall.AssignmentIds.Add(h.Workers[0].AssignmentId); MutationHarness.Digest(recall);
        var result = await h.Service.MutateAsync(1, recall, default);
        Assert.NotEmpty(result.ParcelIds); Assert.Equal(10, await h.ResourceCount());
        var row = await h.Row(); Assert.Equal("Closed", row.State); Assert.False(row.OccupiesSlot);
        Assert.Equal(0, row.Stock); Assert.Equal("0", row.FractionNumerator);
        Assert.False(await h.Db.OutpostReservations.AnyAsync(r => r.Active));
        Assert.False(await h.Db.OutpostAssignments.AnyAsync(a => a.Active));
    }

    [Fact]
    public async Task Owner_preview_exposes_committed_roster_rates_capacity_and_pinned_geography()
    {
        using var h = await MutationHarness.Create(2); h.F.Time.Advance(TimeSpan.FromMinutes(1));
        var view = await h.Service.StateAsync(1, new() { Scope = h.Scope.Clone() }, default);
        var row = Assert.Single(view.Outposts);
        Assert.Equal("Active", row.Lifecycle); Assert.Equal(1000, row.Capacity);
        Assert.Equal(40d, row.UnitsPerHour); Assert.True(row.HasFraction);
        Assert.Equal(h.F.Request.ManifestId, row.ManifestId); Assert.Equal(h.F.Request.ManifestDigest, row.ManifestDigest);
        Assert.Equal(2, row.Workers.Count); Assert.All(row.Workers, worker => {
            Assert.Contains(worker.PawnCustodyId, row.PawnCustodyIds);
            Assert.Contains(worker.AssignmentId, row.AssignmentIds); Assert.Equal(20d, worker.UnitsPerHour);
        });
    }

    [Fact]
    public async Task Close_keeps_history_and_does_not_refund_500()
    {
        using var h = await MutationHarness.Create(); var request = h.Request(OutpostMutationKind.Close);
        var first = await h.Service.MutateAsync(1, request, default);
        h.F.Freeze.Frozen = true; var second = await h.Service.MutateAsync(1, request, default);
        Assert.Equal(first, second); Assert.Equal(200, await h.F.Wallet.Balance(1));
        Assert.Single(await h.Db.Outposts.ToListAsync()); Assert.Single(await h.Db.OutpostAssignments.ToListAsync());
        Assert.Single(await h.Db.PawnCustody.ToListAsync());
        Assert.Equal(2, await h.Db.OutpostOperations.CountAsync());
        Assert.Single(await h.Db.WalletEntries.Where(e => e.Kind == "outpost-found").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switch_settles_before_empty_check_and_requires_every_worker(bool ineligibleWorker)
    {
        using var h = await MutationHarness.Create(2, secondCanMine: !ineligibleWorker);
        if (!ineligibleWorker) h.F.Time.Advance(TimeSpan.FromHours(1));
        var request = h.Request(OutpostMutationKind.Switch); request.Profile = OutpostProfile.Steel; MutationHarness.Digest(request);
        var error = await Assert.ThrowsAsync<RpcException>(() => h.Service.MutateAsync(1, request, default));
        Assert.Equal(ineligibleWorker ? ErrorCode.OutpostIneligible : ErrorCode.OutpostConflict, error.ErrorDetail()?.Code);
        Assert.Equal((int)OutpostProfile.Wood, (await h.Row()).Profile);
        Assert.Equal(0, (await h.Row()).Stock); // Failed mutation rolls settlement back, too.
    }

    [Fact]
    public async Task Fraction_discard_requires_explicit_confirmation_and_replay_keeps_new_clock()
    {
        using var h = await MutationHarness.Create(); h.F.Time.Advance(TimeSpan.FromMinutes(1));
        var request = h.Request(OutpostMutationKind.Switch); request.Profile = OutpostProfile.Steel;
        request.ConfirmFractionDiscard = false; MutationHarness.Digest(request);
        await Assert.ThrowsAsync<RpcException>(() => h.Service.MutateAsync(1, request, default));
        request.ConfirmFractionDiscard = true; MutationHarness.Digest(request);
        var first = await h.Service.MutateAsync(1, request, default);
        Assert.Equal("0", (await h.Row()).FractionNumerator);
        h.F.Time.Advance(TimeSpan.FromHours(1)); Assert.Equal(first, await h.Service.MutateAsync(1, request, default));
        var state = await h.Service.StateAsync(1, new() { Scope = h.Scope.Clone() }, default);
        Assert.Equal(10, Assert.Single(state.Outposts).Stock);
    }

    [Fact]
    public async Task Freeze_debt_threat_do_not_block_own_returns()
    {
        using var h = await MutationHarness.Create(); h.F.Freeze.Frozen = true;
        await h.Db.Wallets.ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, -700));
        h.Db.Raids.Add(new Rimlink.Server.Storage.Entities.Raid { AttackerPlayerId = 1, TargetPlayerId = 1, State = RaidStateDb.Queued });
        await h.Db.SaveChangesAsync();
        h.F.Time.Advance(TimeSpan.FromHours(1));
        var result = await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default);
        Assert.Equal(OutpostOperationStatus.Committed, result.Status); Assert.Equal(20, await h.ResourceCount());
    }

    [Fact]
    public async Task Same_id_changed_intent_and_old_boundary_are_rejected_before_replay()
    {
        using var h = await MutationHarness.Create(); var request = h.Request(OutpostMutationKind.Claim);
        await h.Service.MutateAsync(1, request, default);
        var changed = request.Clone(); changed.Kind = OutpostMutationKind.Close; MutationHarness.Digest(changed);
        Assert.Equal(ErrorCode.RequestIntentMismatch, (await Assert.ThrowsAsync<RpcException>(() => h.Service.MutateAsync(1, changed, default))).ErrorDetail()?.Code);
        await h.Db.Players.ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, 1));
        Assert.Equal(ErrorCode.OutpostRestoreBoundaryMismatch, (await Assert.ThrowsAsync<RpcException>(() => h.Service.MutateAsync(1, request, default))).ErrorDetail()?.Code);
    }
}

internal sealed class MutationHarness : IDisposable
{
    public AssignmentHarness F { get; }
    public RimlinkDbContext Db => F.Db;
    public OutpostScopeMessage Scope => F.Request.Scope;
    public List<OutpostWorkerCapture> Workers { get; } = [];
    public EvidenceSave Post { get; private set; } = null!;
    public OutpostService Service { get; }
    public OutpostParcels Parcels { get; }
    public MailboxService Mailbox { get; }
    private MutationHarness(AssignmentHarness f)
    {
        F = f; var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        Mailbox = new(Db, events, F.Wallet, Options.Create(new EconomyOptions { MaxPendingParcels = 1 }));
        var clock = new OutpostClock(F.Time);
        Parcels = new(Db, F.H.H.Authority, F.Service, Mailbox, clock);
        Service = new(Db, F.H.H.Authority, F.Service, new(Db, F.H.H.Authority, clock, F.H.H.Files), Parcels, clock);
    }
    public static async Task<MutationHarness> Create(int count = 1, bool secondCanMine = true, bool prepareOnly = false)
    {
        var h = new MutationHarness(await AssignmentHarness.Create());
        var metadata = h.F.Pre.Recovery.Outposts.Metadata.Clone(); metadata.WorkerCaptures.Clear(); metadata.Caravans.Clear();
        for (int n = 0; n < count; n++)
        {
            var worker = h.F.Request.Workers[0].Clone(); worker.Pawn.MarketValueX100 = 10000;
            if (n > 0)
            {
                worker.PawnCustodyId = Guid.NewGuid().ToString("D"); worker.AssignmentId = Guid.NewGuid().ToString("D");
                worker.Pawn.Registry.PawnCustodyId = worker.PawnCustodyId; worker.Pawn.Registry.PawnReferenceToken = "Thing_Human" + (n + 1);
                worker.MiningCapable = secondCanMine; metadata.Registry.Add(worker.Pawn.Registry.Clone());
            }
            worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray()));
            h.Workers.Add(worker); metadata.WorkerCaptures.Add(worker.Clone());
        }
        h.F.Request.Workers.Clear(); h.F.Request.Workers.Add(h.Workers);
        var caravan = h.F.Request.Caravan; caravan.RosterReferenceTokens.Clear(); caravan.SelectedCustodyIds.Clear();
        caravan.RosterReferenceTokens.Add(h.Workers.Select(w => w.Pawn.Registry.PawnReferenceToken));
        caravan.SelectedCustodyIds.Add(h.Workers.Select(w => w.PawnCustodyId)); metadata.Caravans.Add(caravan.Clone());
        var pre = h.F.H.Snapshot(metadata); await h.F.H.Accept(pre);
        h.F.Request.PreRemoval = AssignmentHarness.SnapshotProof(pre); h.F.Request.IntentDigest = OutpostRequestIdentity.Digest(h.F.Request);
        var prepared = await h.F.Service.PrepareAsync(1, h.F.Request, default);
        if (prepareOnly) { h.Post = pre; return h; }
        foreach (var registry in metadata.Registry)
        { registry.Tombstone = true; registry.PawnReferenceToken = ""; registry.AssignmentId = h.Workers.Single(w => w.PawnCustodyId == registry.PawnCustodyId).AssignmentId; }
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == prepared.TransitionId);
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = h.F.Request.ClientRequestId,
            IntentDigest = h.F.Request.IntentDigest, Snapshot = h.F.Request.PreRemoval.Clone(),
            PawnCustodyIds = { h.Workers.Select(w => w.PawnCustodyId) }, AssignmentIds = { h.Workers.Select(w => w.AssignmentId) } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        h.Post = h.F.H.Snapshot(metadata); await h.F.H.Accept(h.Post);
        await h.F.Service.CommitAsync(1, h.F.Proof(h.Post), default); return h;
    }
    public async Task<OutpostRow> Row() => await Db.Outposts.AsNoTracking().SingleAsync(o => o.Id == F.Request.OutpostId);
    public OutpostMutationRequest Request(OutpostMutationKind kind)
    {
        var row = Db.Outposts.AsNoTracking().Single(o => o.Id == F.Request.OutpostId);
        var r = new OutpostMutationRequest { Scope = Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"), Kind = kind,
            ExpectedRevision = row.Revision, OutpostId = row.Id, Profile = (OutpostProfile)row.Profile, Tile = row.Tile, SurfaceId = "planet",
            ManifestId = row.ManifestId, ManifestDigest = ByteString.CopyFrom(row.ManifestDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(Post),
            ConfirmFractionDiscard = true }; Digest(r); return r;
    }
    public static void Digest(OutpostMutationRequest r)
    { var copy = r.Clone(); copy.IntentDigest = ByteString.Empty; r.IntentDigest = ByteString.CopyFrom(SHA256.HashData(copy.ToByteArray())); }
    public async Task<long> ResourceCount() => (await Db.OutpostParcelLinks.AsNoTracking().Select(p => p.Payload).ToListAsync())
        .Select(OutpostParcelPayload.Parser.ParseFrom).SelectMany(p => p.Resources).Sum(r => r.Count);
    public void Dispose() => F.Dispose();
}
