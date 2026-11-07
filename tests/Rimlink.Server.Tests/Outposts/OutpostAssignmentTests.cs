using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Events;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Sessions;
using Rimlink.Server.Hosting;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostAssignmentTests
{
    [Fact]
    public async Task Found_prepare_reserves_but_does_not_charge_or_produce()
    {
        using var f = await AssignmentHarness.Create();
        var reply = await f.Service.PrepareAsync(1, f.Request, default);
        Assert.Equal(OutpostOperationStatus.Prepared, reply.Status);
        var transition = await f.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == reply.TransitionId);
        Assert.NotEmpty(transition.PredecessorId!); Assert.Equal(transition.PredecessorId, reply.PredecessorId);
        Assert.Equal(700, await f.Wallet.Balance(1)); Assert.Equal(200, await f.Wallet.Available(1));
        Assert.Empty(await f.Wallet.History(1));
        var outpost = Assert.Single(await f.Db.Outposts.AsNoTracking().ToListAsync());
        Assert.Equal("Prepared", outpost.State); Assert.Equal(0, outpost.Stock);
        Assert.True(Assert.Single(await f.Db.OutpostReservations.AsNoTracking().ToListAsync()).Active);
        Assert.Equal(f.Request.Workers[0].Pawn.ToByteArray(), Assert.Single(await f.Db.OutpostAssignments.AsNoTracking().ToListAsync()).Blob);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.H.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Personal_inventory_must_be_captured_not_silently_discarded(bool captured)
    {
        using var f = await AssignmentHarness.Create();
        f.Request.Caravan.SelectedCargoEmpty = false; f.Request.Caravan.SelectedCargoCaptured = captured;
        var metadata = f.Pre.Recovery.Outposts.Metadata.Clone(); metadata.Caravans[0] = f.Request.Caravan.Clone();
        var pre = f.H.Snapshot(metadata); await f.H.Accept(pre); f.Request.PreRemoval = AssignmentHarness.SnapshotProof(pre);
        f.Request.IntentDigest = OutpostRequestIdentity.Digest(f.Request);
        if (captured) Assert.Equal(OutpostOperationStatus.Prepared, (await f.Service.PrepareAsync(1, f.Request, default)).Status);
        else await Assert.ThrowsAsync<RpcException>(() => f.Service.PrepareAsync(1, f.Request, default));
    }

    [Fact]
    public async Task Same_request_replay_precedes_freeze_but_different_intent_is_rejected()
    {
        using var f = await AssignmentHarness.Create();
        var first = await f.Service.PrepareAsync(1, f.Request, default); f.Freeze.Frozen = true;
        Assert.Equal(first, await f.Service.PrepareAsync(1, f.Request, default));
        var changed = f.Request.Clone(); changed.Workers[0].Plants = 19; changed.IntentDigest = OutpostRequestIdentity.Digest(changed);
        var error = await Assert.ThrowsAsync<RpcException>(() => f.Service.PrepareAsync(1, changed, default));
        Assert.Equal(ErrorCode.RequestIntentMismatch, error.ErrorDetail()?.Code);
        Assert.Single(await f.Db.OutpostOperations.ToListAsync());
    }

    [Fact]
    public async Task Commit_requires_exact_post_removal_and_charges_only_once()
    {
        using var f = await AssignmentHarness.Create();
        var prepared = await f.Service.PrepareAsync(1, f.Request, default);
        await Assert.ThrowsAsync<RpcException>(() => f.Service.CommitAsync(1, f.Proof(f.Pre), default));
        var removed = await f.Removal(prepared); f.Freeze.Frozen = true;
        var result = await f.Service.CommitAsync(1, f.Proof(removed), default);
        Assert.Equal(OutpostOperationStatus.Committed, result.Status);
        Assert.Equal(result, await f.Service.CommitAsync(1, f.Proof(removed), default));
        Assert.Equal(200, await f.Wallet.Balance(1)); Assert.Equal(-500, Assert.Single(await f.Wallet.History(1)).Delta);
        Assert.Equal("Assigned", Assert.Single(await f.Db.PawnCustody.AsNoTracking().ToListAsync()).LocationKind);
        var outpost = Assert.Single(await f.Db.Outposts.AsNoTracking().ToListAsync());
        Assert.Equal("Active", outpost.State); Assert.Equal(0, outpost.Stock); Assert.Equal(f.Time.Now.UtcDateTime, outpost.CheckpointUtc);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Scope_is_checked_before_terminal_replay()
    {
        using var f = await AssignmentHarness.Create();
        var prepared = await f.Service.PrepareAsync(1, f.Request, default); var removed = await f.Removal(prepared);
        await f.Service.CommitAsync(1, f.Proof(removed), default);
        var stale = f.Proof(removed); stale.Scope.SaveWriteEpoch++;
        await Assert.ThrowsAsync<RpcException>(() => f.Service.CommitAsync(1, stale, default));
        Assert.Equal(200, await f.Wallet.Balance(1));
    }

    [Theory]
    [InlineData("adult")]
    [InlineData("free")]
    [InlineData("owner")]
    [InlineData("human")]
    [InlineData("downed")]
    [InlineData("mental")]
    [InlineData("work")]
    [InlineData("health")]
    public async Task Ineligible_worker_leaves_every_reserve_untouched(string fault)
    {
        using var f = await AssignmentHarness.Create(fault);
        var error = await Assert.ThrowsAsync<RpcException>(() => f.Service.PrepareAsync(1, f.Request, default));
        Assert.Equal(ErrorCode.OutpostIneligible, error.ErrorDetail()?.Code);
        Assert.Empty(await f.Db.OutpostOperations.ToListAsync()); Assert.Empty(await f.Db.Outposts.ToListAsync());
        Assert.Equal(700, await f.Wallet.Available(1));
    }

    [Fact]
    public async Task Caravan_attestation_and_worker_blob_are_bound_to_pre_snapshot()
    {
        using var f = await AssignmentHarness.Create();
        var changed = f.Request.Clone(); changed.Caravan.Tile = 1; changed.IntentDigest = OutpostRequestIdentity.Digest(changed);
        await Assert.ThrowsAsync<RpcException>(() => f.Service.PrepareAsync(1, changed, default));
        changed = f.Request.Clone(); changed.Workers[0].Pawn.Name = "changed";
        changed.Workers[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(changed.Workers[0].Pawn.ToByteArray()));
        changed.IntentDigest = OutpostRequestIdentity.Digest(changed);
        await Assert.ThrowsAsync<RpcException>(() => f.Service.PrepareAsync(1, changed, default));
        Assert.Empty(await f.Db.OutpostOperations.ToListAsync());
    }

    [Fact]
    public async Task Old_pre_snapshot_does_not_prove_no_removal_and_never_auto_expires()
    {
        using var f = await AssignmentHarness.Create();
        await f.Service.PrepareAsync(1, f.Request, default); f.Time.Advance(TimeSpan.FromDays(90));
        var cancel = f.Cancel(OutpostCancelKind.AbortNoRemoval, f.Pre);
        var result = await f.Service.CancelAsync(1, cancel, default);
        Assert.Equal(OutpostOperationStatus.RecoveryRequired, result.Status);
        Assert.Equal(500, (await f.Db.OutpostOperations.AsNoTracking().SingleAsync()).ReservedSilver);
        Assert.True((await f.Db.OutpostReservations.AsNoTracking().SingleAsync()).Active);
    }

    [Fact]
    public async Task No_removal_cancel_requires_fresh_fence_receipt_and_blocks_late_commit()
    {
        using var f = await AssignmentHarness.Create();
        var prepared = await f.Service.PrepareAsync(1, f.Request, default);
        await f.H.Accept(f.H.Snapshot(f.Pre.Recovery.Outposts.Metadata.Clone()));
        var fence = await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.AbortNoRemoval, f.Pre), default);
        Assert.Equal(OutpostOperationStatus.RecoveryRequired, fence.Status);
        Assert.NotEqual(prepared.TransitionId, fence.TransitionId);
        Assert.Equal("CancelFenced", (await f.Db.OutpostOperations.AsNoTracking().SingleAsync()).State);
        var safe = await f.NoRemoval(fence);
        var current = f.H.Snapshot(safe.Recovery.Outposts.Metadata.Clone());
        await f.H.Accept(current);
        var stale = await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.AbortNoRemoval, safe), default);
        Assert.Equal(OutpostOperationStatus.RecoveryRequired, stale.Status);
        Assert.Equal("CancelFenced", (await f.Db.OutpostOperations.AsNoTracking().SingleAsync()).State);
        var result = await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.AbortNoRemoval, current), default);
        Assert.Equal(OutpostOperationStatus.Cancelled, result.Status);
        Assert.Equal(result, await f.Service.CommitAsync(1, f.Proof(safe), default));
        Assert.Equal(700, await f.Wallet.Available(1)); Assert.Empty(await f.Wallet.History(1));
        Assert.Equal("Home", (await f.Db.PawnCustody.AsNoTracking().SingleAsync()).LocationKind);
        Assert.False((await f.Db.OutpostReservations.AsNoTracking().SingleAsync()).Active);
    }

    [Fact]
    public async Task Accepted_no_removal_receipt_is_historical_not_a_permanent_pawn_immortality_condition()
    {
        using var f = await AssignmentHarness.Create(); await f.Service.PrepareAsync(1, f.Request, default);
        var fence = await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.AbortNoRemoval), default);
        var safe = await f.NoRemoval(fence); await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.AbortNoRemoval, safe), default);
        var later = safe.Recovery.Outposts.Metadata.Clone(); later.Registry[0].Tombstone = true; later.Registry[0].PawnReferenceToken = "";
        var death = f.H.Snapshot(later); await f.H.Accept(death);
        Assert.True((await f.Db.PawnCustody.AsNoTracking().SingleAsync()).Tombstone);
        Assert.Equal(OutpostStartupState.Ready, (await f.H.H.Authority.OpenAsync(default)).State);
    }
    [Fact]
    public async Task Removed_cancel_returns_exact_workers_in_one_own_mail_batch_without_charge()
    {
        using var f = await AssignmentHarness.Create();
        var prepared = await f.Service.PrepareAsync(1, f.Request, default); var removed = await f.Removal(prepared);
        var result = await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.ReturnAfterRemoval, removed), default);
        Assert.Equal(OutpostOperationStatus.Cancelled, result.Status);
        Assert.Equal(result, await f.Service.CancelAsync(1, f.Cancel(OutpostCancelKind.ReturnAfterRemoval, removed), default));
        Assert.Equal(result, await f.Service.CommitAsync(1, f.Proof(removed), default));
        var parcel = Assert.Single(await f.Db.Parcels.AsNoTracking().ToListAsync());
        Assert.Equal(1, parcel.ToPlayerId); Assert.Null(parcel.FromPlayerId);
        Assert.Single(PawnList.Parser.ParseFrom(parcel.Pawns).Pawns);
        Assert.Equal(parcel.Id, Assert.Single(result.ParcelIds));
        var link = await f.Db.OutpostParcelLinks.AsNoTracking().SingleAsync();
        var mailbox = new MailboxService(f.Db, new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance), f.Wallet, Options.Create(new EconomyOptions()), f.H.H.Files);
        var wire = await mailbox.ToProto(parcel);
        Assert.Null(OutpostPayloadCompatibility.Refusal(wire, 1));
        Assert.Equal(1, wire.OutpostPayload.ChildCount); Assert.True(wire.OutpostPayload.ValuationRevision > 0);
        Assert.Equal("ReturnPending", (await f.Db.PawnCustody.AsNoTracking().SingleAsync()).LocationKind);
        Assert.Equal(700, await f.Wallet.Available(1)); Assert.Empty(await f.Wallet.History(1));
        Assert.Equal(OutpostStartupState.Ready, (await f.H.H.Authority.OpenAsync(default)).State);
    }
}

internal sealed class AssignmentHarness : IDisposable
{
    public SnapshotHarness H { get; }
    public Rimlink.Server.Storage.RimlinkDbContext Db => H.H.Db;
    public OutpostAssignmentService Service { get; }
    public WalletService Wallet { get; }
    public FreezeState Freeze { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public OutpostPrepareRequest Request { get; private set; } = null!;
    public EvidenceSave Pre { get; private set; } = null!;
    private AssignmentHarness(SnapshotHarness h)
    {
        H = h; var events = new EventBus(new SessionRegistry(), NullLogger<EventBus>.Instance);
        Wallet = new(Db, events); var options = Options.Create(new EconomyOptions());
        Service = new(Db, h.H.Authority, h.Guard, new(Db, h.H.Authority, new(Time), h.H.Files),
            new(Db, h.H.Authority), Wallet, new(options, Freeze, new(Db, events)), new(Db, options), new(Time));
    }
    public static async Task<AssignmentHarness> Create(string? fault = null)
    {
        var f = new AssignmentHarness(await SnapshotHarness.Create());
        f.Db.ModpackDefs.AddRange(new[] { "TemperateForest", "Steel", "WoodLog", "Colonist" }.Select(d => new ModpackDef { DefName = d }));
        f.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 1, Balance = 700 }); await f.Db.SaveChangesAsync();
        var manifests = new OutpostManifestService(f.Db, f.H.H.Authority, new(f.Time), f.H.H.Files);
        var document = new OutpostManifestDocument { SchemaVersion = 1, Binding = await manifests.BindingAsync(Actor.Api, default),
            GameVersion = "1.7", GameDefsSha256 = ByteString.CopyFrom(new byte[32]), MainSurfaceId = "planet", TileCount = 1, ValuationVersion = "v1",
            Tiles = { new OutpostManifestTile { SurfaceId = "planet", BiomeDef = "TemperateForest", Land = true, CaravanPassable = true, NaturalTrees = true } },
            Resources = { new OutpostResourceValuation { DefName = "Steel", UnitValueX100 = 190 }, new OutpostResourceValuation { DefName = "WoodLog", UnitValueX100 = 120 } } };
        var manifest = await manifests.StageAsync(Actor.Api, document, default); await manifests.ConfirmAsync(Actor.Api, manifest.ManifestId, manifest.Digest, default);
        var metadata = f.H.Registered();
        var worker = new OutpostWorkerCapture { PawnCustodyId = SnapshotHarness.Custody, AssignmentId = SnapshotHarness.Assignment,
            Pawn = new PawnBlob { KindDef = "Colonist", Name = "Nova", Humanlike = true, AgeYears = 25,
                ScribeXmlZstd = ByteString.CopyFromUtf8("opaque-valid-capture"), Registry = metadata.Registry[0].Clone() },
            Plants = 10, Mining = 10, Consciousness = 1, Manipulation = 1, Moving = 1, PlantsCapable = true, MiningCapable = true,
            AdultLifeStage = true, FreeColonist = true, PlayerOwned = true, BaselineHuman = true };
        switch (fault) { case "adult": worker.AdultLifeStage = false; break; case "free": worker.FreeColonist = false; break;
            case "owner": worker.PlayerOwned = false; break; case "human": worker.BaselineHuman = false; break;
            case "downed": worker.Downed = true; break; case "mental": worker.MentalState = true; break;
            case "work": worker.PlantsCapable = false; break; case "health": worker.Moving = 0; break; }
        worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray()));
        string requestId = Guid.NewGuid().ToString("N");
        var caravan = new OutpostCaravanAttestation { CaravanId = "Caravan_1", SurfaceId = "planet", SelectedCargoEmpty = true,
            RosterReferenceTokens = { "Thing_Human1" }, SelectedCustodyIds = { SnapshotHarness.Custody },
            ManifestDigest = ByteString.CopyFrom(Convert.FromHexString(manifest.Digest)), ClientRequestId = requestId, GameBranchId = Guid.NewGuid().ToString("D") };
        metadata.WorkerCaptures.Add(worker.Clone()); metadata.Caravans.Add(caravan.Clone()); f.Pre = f.H.Snapshot(metadata); await f.H.Accept(f.Pre);
        f.Request = new() { Scope = metadata.Scope.Clone(), ClientRequestId = requestId, Kind = OutpostPrepareKind.Found,
            ManifestId = manifest.ManifestId, ManifestDigest = caravan.ManifestDigest, OutpostId = Guid.NewGuid().ToString("D"),
            Profile = OutpostProfile.Wood, SurfaceId = "planet", Workers = { worker }, Caravan = caravan, PreRemoval = SnapshotProof(f.Pre) };
        f.Request.IntentDigest = OutpostRequestIdentity.Digest(f.Request);
        return f;
    }
    public static OutpostSnapshotProof SnapshotProof(EvidenceSave save) => new() { Scope = save.Recovery.Outposts.Metadata.Scope.Clone(),
        SnapshotId = save.Version.SnapshotId!, RawSha256 = ByteString.CopyFrom(save.Version.RawSha256!) };
    public OutpostProofRequest Proof(EvidenceSave save) => new() { Scope = Request.Scope.Clone(), ClientRequestId = Request.ClientRequestId,
        IntentDigest = Request.IntentDigest, PostRemoval = SnapshotProof(save), ExpectedRevision = 1 };
    public OutpostCancelRequest Cancel(OutpostCancelKind kind, EvidenceSave? save = null) => new() { Scope = Request.Scope.Clone(),
        ClientRequestId = Request.ClientRequestId, IntentDigest = Request.IntentDigest, Kind = kind,
        CancellationProof = save is null ? null : SnapshotProof(save), ExpectedRevision = 1, ContinuousBranchId = Request.Caravan.GameBranchId };
    public async Task<EvidenceSave> Removal(OutpostOperationReply prepared)
    {
        var metadata = Pre.Recovery.Outposts.Metadata.Clone(); metadata.Registry[0].Tombstone = true;
        metadata.Registry[0].PawnReferenceToken = ""; metadata.Registry[0].AssignmentId = SnapshotHarness.Assignment;
        return await Receipt(metadata, prepared, OutpostCancelKind.Unspecified);
    }
    public Task<EvidenceSave> NoRemoval(OutpostOperationReply fence) => Receipt(Pre.Recovery.Outposts.Metadata.Clone(), fence, OutpostCancelKind.AbortNoRemoval);
    private async Task<EvidenceSave> Receipt(OutpostSaveMetadata metadata, OutpostOperationReply reply, OutpostCancelKind cancel)
    {
        var transition = await Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == reply.TransitionId);
        metadata.Receipts.Add(new OutpostTransitionReceipt() { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = Request.ClientRequestId, IntentDigest = Request.IntentDigest,
            Snapshot = Request.PreRemoval.Clone(), PawnCustodyIds = { SnapshotHarness.Custody }, AssignmentIds = { SnapshotHarness.Assignment }, CancellationKind = cancel,
            NoRemovalBranchId = cancel == OutpostCancelKind.AbortNoRemoval ? Request.Caravan.GameBranchId : "" });
        metadata.Frontier.Add(new OutpostFrontierEntry() { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        var save = H.Snapshot(metadata); await H.Accept(save); return save;
    }
    public void Dispose() => H.Dispose();
}
