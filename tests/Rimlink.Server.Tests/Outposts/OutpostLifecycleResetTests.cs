using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Admin;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Trades;
using Rimlink.Server.Storage.Entities;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostLifecycleTests
{
    [Fact]
    public async Task Clean_reset_advances_generation_without_migrating_old_assets_or_evidence()
    {
        using var f = await LifecycleFixture.Create();
        await CloseAndReceive(f);
        var before = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(v => v.SnapshotId).ToArrayAsync();
        string oldLinks = JsonSerializer.Serialize(links);
        using var scope = f.Host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1);
        var player = await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1);
        Assert.Equal(1, player.SaveGeneration); Assert.Equal(0, player.SaveWriteEpoch); Assert.Equal(0, player.LastOutgoingTick);
        Assert.Empty(await f.H.Db.SaveVersions.AsNoTracking().ToListAsync());
        Assert.Empty(await f.H.Db.WorldObjects.AsNoTracking().Where(o => o.OwnerPlayerId == 1).ToListAsync());
        Assert.Equal(oldLinks, JsonSerializer.Serialize(await f.H.Db.OutpostSnapshotLinks.AsNoTracking().OrderBy(v => v.SnapshotId).ToArrayAsync()));
        var oldCustody = await f.H.Db.PawnCustody.AsNoTracking().SingleAsync();
        Assert.Equal(0, oldCustody.SaveGeneration); Assert.Equal("Home", oldCustody.LocationKind);
        Assert.Empty(await f.H.Db.Outposts.AsNoTracking().Where(o => o.SaveGeneration == 1).ToListAsync());
        var operation = Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        Assert.Equal("Reset", operation.Kind); Assert.Equal("Completed", operation.Phase);
        var after = await f.H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        Assert.Equal(before.InstallationId, after.InstallationId); Assert.Equal(before.WorldId, after.WorldId);
        Assert.Equal(before.RestoreBoundary, after.RestoreBoundary); Assert.NotEqual(before.HeadId, after.HeadId);
        await AssertOriginalEvidence(f, links);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData("Prepared", false)] [InlineData("EvidenceRetained", false)]
    [InlineData("Destructive", false)] [InlineData("SourceCleanup", false)] [InlineData("Completed", true)]
    public async Task Crash_each_reset_phase_retains_original_evidence_and_blocks_unfinished_startup(string phase, bool complete)
    {
        using var f = await LifecycleFixture.Create(); await CloseAndReceive(f);
        var links = await f.H.Db.OutpostSnapshotLinks.AsNoTracking().ToArrayAsync();
        using var scope = f.Host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OutpostLifecycle>().Checkpoint = at =>
        { if (at == phase) throw new IOException("simulated reset interruption: " + phase); };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1));
        Assert.Equal(complete ? OutpostStartupState.Ready : OutpostStartupState.Maintenance,
            (await f.H.F.H.H.Authority.OpenAsync(default)).State);
        var row = Assert.Single(await f.H.Db.OutpostLifecycleOperations.AsNoTracking().ToListAsync());
        Assert.Equal(phase == "SourceCleanup" ? "Destructive" : phase, row.Phase);
        Assert.Equal(phase is "Destructive" or "SourceCleanup" or "Completed" ? 1 : 0,
            (await f.H.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).SaveGeneration);
        await AssertOriginalEvidence(f, links);
    }

    [Fact]
    public async Task Recreate_blocked_last_owner_has_no_effect_on_clean_first_owner()
    {
        using var f = await LifecycleFixture.Create(); await CloseAndReceive(f);
        await PrepareSecondOwner(f);
        string before = await f.Cut();
        using var scope = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().RecreateWorld(Actor.Api));
        Assert.Equal(before, await f.Cut());
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Reset_archived_owner_requires_restore_before_any_effect()
    {
        using var f = await LifecycleFixture.Create(); await f.Archive();
        string before = await f.Cut(); using var scope = f.Host.Services.CreateScope();
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1));
        Assert.Equal(before, await f.Cut());
    }

    [Fact]
    public async Task Reset_returns_other_owners_tagged_trade_under_the_existing_lifecycle_barrier()
    {
        using var f = await LifecycleFixture.Create(); await CloseAndReceive(f);
        var h = f.H;
        h.Db.Accounts.Add(new Account { Id = 2, Player = new Player { Id = 2, Name = "Other" } });
        await h.Db.SaveChangesAsync();
        var metadata = h.F.H.Metadata(); metadata.Scope.OwnerPlayerId = 2;
        var pawn = h.Workers[0].Pawn.Clone();
        pawn.Registry.Scope = metadata.Scope.Clone(); pawn.Registry.PawnCustodyId = Guid.NewGuid().ToString("D");
        pawn.Registry.AssignmentId = ""; pawn.Registry.PawnReferenceToken = "Thing_Other"; pawn.Registry.Tombstone = false;
        metadata.Registry.Add(pawn.Registry.Clone());
        await h.F.H.Accept(h.F.H.Snapshot(metadata));
        var request = new TradeOpenRequest { PartnerId = 1, ClientRequestId = Guid.NewGuid().ToString("N"),
            GameTick = 1000, Pawns = new PawnList { Pawns = { pawn } } };
        var capture = OrdinaryCapture.Create(request, Guid.NewGuid().ToString("N"), metadata.Scope);
        metadata.Registry[0].Tombstone = true; metadata.OrdinaryOutgoing.Add(capture);
        await h.F.H.Accept(h.F.H.Snapshot(metadata, removed: [capture.LocalOperationId]));
        using var scope = f.Host.Services.CreateScope();
        var trade = await scope.ServiceProvider.GetRequiredService<TradeService>().Open(2, request);
        Assert.Equal("Escrow", (await h.Db.PawnCustody.AsNoTracking().SingleAsync(c => c.PlayerId == 2)).LocationKind);
        await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TradeStatusDb.Cancelled, (await h.Db.Trades.AsNoTracking().SingleAsync(t => t.Id == trade.Id)).Status);
        var returned = await h.Db.PawnCustody.AsNoTracking().SingleAsync(c => c.PlayerId == 2);
        Assert.Equal("ReturnPending", returned.LocationKind); Assert.Equal(pawn.Registry.PawnCustodyId, returned.Id);
        var parcel = Assert.Single(await h.Db.Parcels.AsNoTracking().Where(p => p.ToPlayerId == 2 && p.DeliveredAt == null).ToArrayAsync());
        Assert.Equal(pawn.Registry.PawnCustodyId, Assert.Single(PawnList.Parser.ParseFrom(parcel.Pawns).Pawns).Registry.PawnCustodyId);
        Assert.Equal(1, (await h.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 1)).SaveGeneration);
        Assert.Equal(0, (await h.Db.Players.AsNoTracking().SingleAsync(p => p.Id == 2)).SaveGeneration);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.F.H.H.Authority.OpenAsync(default));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Reset_projection_allows_only_real_accepted_current_generation_snapshots(bool accepted)
    {
        using var f = await LifecycleFixture.Create(); await CloseAndReceive(f);
        using (var scope = f.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1);
        var metadata = f.H.F.H.Metadata(); metadata.Scope.SaveGeneration = 1;
        var fresh = f.H.F.H.Snapshot(metadata);
        if (accepted) await f.H.F.H.Accept(fresh);
        else
        {
            // Actual bytes alone are not an accepted custody boundary.
            f.H.F.H.H.Files.WriteAtomically(f.H.F.H.H.Files.FullPath(1, fresh.Version.Number), fresh.Packed);
            f.H.Db.SaveVersions.Add(fresh.Version); await f.H.Db.SaveChangesAsync();
        }
        Assert.Equal(accepted ? new OutpostStartupResult(OutpostStartupState.Ready)
            : new OutpostStartupResult(OutpostStartupState.Maintenance, "lifecycle-reset-projection-mismatch"),
            await f.H.F.H.H.Authority.OpenAsync(default));
    }

    [Fact]
    public async Task Reset_of_one_owner_does_not_mask_another_owners_archived_projection()
    {
        using var f = await LifecycleFixture.Create(); await CloseAndReceive(f);
        var h = f.H;
        h.Db.Accounts.Add(new Account { Id = 2, Player = new Player { Id = 2, Name = "Archived Other" } });
        h.Db.WorldObjects.Add(new Rimlink.Server.Storage.Entities.WorldObject { OwnerPlayerId = 2,
            Type = WorldObjectTypes.Settlement, Name = "Other Home", Tile = 34, CreatedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        var metadata = h.F.H.Metadata(); metadata.Scope.OwnerPlayerId = 2;
        await h.F.H.Accept(h.F.H.Snapshot(metadata));
        long archiveId;
        using (var scope = f.Host.Services.CreateScope())
            archiveId = Assert.IsType<Rimlink.Server.Application.World.ArchiveOutcome.Archived>(
                await scope.ServiceProvider.GetRequiredService<Rimlink.Server.Application.World.ColonyArchive>().Archive(2, "admin", Actor.System)).ArchiveId;
        using (var scope = f.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AdminOperations>().ResetPlayer(Actor.Api, 1);
        Assert.Equal(new OutpostStartupResult(OutpostStartupState.Ready), await h.F.H.H.Authority.OpenAsync(default));
        await h.Db.ArchivedColonies.Where(a => a.Id == archiveId).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, ArchiveState.Restored));
        Assert.Equal(OutpostStartupState.Maintenance, (await h.F.H.H.Authority.OpenAsync(default)).State);
    }

    internal static async Task AssertOriginalEvidence(LifecycleFixture f, IReadOnlyList<OutpostSnapshotLink> links)
    {
        var resolver = new OutpostLifecycleEvidence(f.H.Db, f.H.F.H.H.Files);
        foreach (var link in links)
        {
            var (version, archive) = await resolver.ResolveSnapshotAsync(link, default);
            Assert.Equal(link.RawSha256, SHA256.HashData(f.H.F.H.H.Files.ReadEvidence(version, archive)));
            Assert.Equal(link.SaveGeneration, version.SaveGeneration); Assert.Equal(link.SaveWriteEpoch, version.SaveWriteEpoch);
        }
    }

    internal static async Task CloseAndReceive(LifecycleFixture f)
    {
        var h = f.H;
        long parcel = Assert.Single((await h.Service.MutateAsync(1, h.Request(OutpostMutationKind.Close), default)).ParcelIds);
        var link = await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == parcel);
        var request = new OutpostDeliveryRequest { Scope = h.Scope.Clone(), ClientRequestId = Guid.NewGuid().ToString("N"),
            ParcelId = parcel, PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(h.Post) };
        var reply = await h.Parcels.BeginDeliveryAsync(1, request, default);
        var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == reply.TransitionId);
        var metadata = h.Post.Recovery.Outposts.Metadata.Clone();
        metadata.Registry[0].Tombstone = false; metadata.Registry[0].PawnReferenceToken = "Thing_HumanReturned";
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(SHA256.HashData(request.ToByteArray())), Snapshot = request.CanonicalSnapshot.Clone(),
            DeliveryAttemptId = reply.AttemptId, ParcelIds = { parcel }, PawnCustodyIds = { h.Workers.Select(w => w.PawnCustodyId) },
            AssignmentIds = { h.Workers.Select(w => w.AssignmentId) } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        await h.F.H.Accept(h.F.H.Snapshot(metadata));
        Assert.Equal("Home", (await h.Db.PawnCustody.AsNoTracking().SingleAsync()).LocationKind);
        Assert.NotNull((await h.Db.OutpostParcelLinks.AsNoTracking().SingleAsync()).AcceptedSnapshotId);
    }

    private static async Task PrepareSecondOwner(LifecycleFixture f)
    {
        var h = f.H; h.Db.Accounts.Add(new Account { Id = 2, Player = new Player { Id = 2, Name = "Second", SaveGeneration = 7, SaveWriteEpoch = 9 } });
        h.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 2, Balance = 500 }); await h.Db.SaveChangesAsync();
        var metadata = h.F.H.Metadata(); metadata.Scope.OwnerPlayerId = 2; metadata.Scope.SaveGeneration = 7; metadata.Scope.SaveWriteEpoch = 9;
        var worker = h.Workers[0].Clone(); worker.PawnCustodyId = Guid.NewGuid().ToString("D"); worker.AssignmentId = Guid.NewGuid().ToString("D");
        worker.Pawn.Registry.PawnCustodyId = worker.PawnCustodyId; worker.Pawn.Registry.Scope = metadata.Scope.Clone();
        worker.Pawn.Registry.PawnReferenceToken = "Thing_Second1"; worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray())); // gitleaks:allow -- synthetic pawn load-id, not an authentication credential.
        metadata.Registry.Add(worker.Pawn.Registry.Clone()); metadata.WorkerCaptures.Add(worker.Clone());
        var request = h.F.Request.Clone(); request.Scope = metadata.Scope.Clone(); request.ClientRequestId = Guid.NewGuid().ToString("N");
        request.OutpostId = Guid.NewGuid().ToString("D"); request.Workers.Clear(); request.Workers.Add(worker);
        request.Caravan.CaravanId = "Caravan_Second"; request.Caravan.ClientRequestId = request.ClientRequestId;
        request.Caravan.RosterReferenceTokens.Clear(); request.Caravan.RosterReferenceTokens.Add("Thing_Second1");
        request.Caravan.SelectedCustodyIds.Clear(); request.Caravan.SelectedCustodyIds.Add(worker.PawnCustodyId);
        metadata.Caravans.Add(request.Caravan.Clone());
        var pre = h.F.H.Snapshot(metadata); await h.F.H.Accept(pre); request.PreRemoval = AssignmentHarness.SnapshotProof(pre);
        request.IntentDigest = OutpostRequestIdentity.Digest(request);
        Assert.Equal(OutpostOperationStatus.Prepared, (await h.F.Service.PrepareAsync(2, request, default)).Status);
    }
}
