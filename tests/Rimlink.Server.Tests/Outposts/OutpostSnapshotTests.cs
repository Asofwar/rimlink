using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Application.Saves;
using Rimlink.Server.Storage.Entities;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OutpostSnapshotTests
{
    [Fact]
    public async Task Stored_valid_exact_link_allows_authority_ready()
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot();
        await h.Accept(save);
        Assert.Single(h.H.Db.OutpostSnapshotLinks);
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
        Assert.False(h.H.Feature.IsAvailable);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task Stored_missing_or_corrupt_bytes_keep_maintenance(string fault)
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot();
        await h.Accept(save);
        string path = h.H.Files.FullPath(1, save.Version.Number);
        if (fault == "missing") File.Delete(path); else File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(OutpostStartupState.Maintenance, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Same_snapshot_id_different_raw_bytes_rejected()
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot();
        await h.Accept(save);
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateHeldAsync(lease, 1,
            save.Header, save.Recovery.Outposts, new byte[32], default));
    }

    [Theory]
    [InlineData("world")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("epoch")]
    [InlineData("boundary")]
    public async Task Wrong_scope_is_rejected_before_acceptance(string fault)
    {
        using var h = await SnapshotHarness.Create();
        var metadata = h.Metadata();
        switch (fault)
        {
            case "world": metadata.Scope.WorldInstanceId = "other-world"; break;
            case "owner": metadata.Scope.OwnerPlayerId = 2; break;
            case "generation": metadata.Scope.SaveGeneration = 9; break;
            case "epoch": metadata.Scope.SaveWriteEpoch = 9; break;
            case "boundary": metadata.Scope.RestoreBoundary = "other-boundary"; break;
        }
        var save = h.Snapshot(metadata);
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateHeldAsync(lease, 1,
            save.Header, save.Recovery.Outposts, save.Version.RawSha256!, default));
        Assert.Empty(h.H.Db.OutpostSnapshotLinks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_next_with_stored_proof_finishes_only_valid_branch(bool corrupt)
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot();
        h.H.Authority.CommitCheckpoint = point => { if (point == "AfterDomainCommit") throw new IOException("owned-stop"); };
        await Assert.ThrowsAsync<IOException>(() => h.Accept(save));
        h.H.Authority.CommitCheckpoint = null;
        if (corrupt) File.Delete(h.H.Files.FullPath(1, save.Version.Number));
        Assert.Equal(corrupt ? OutpostStartupState.Maintenance : OutpostStartupState.Ready,
            (await h.H.Authority.OpenAsync(default)).State);
        using var held = await h.H.Anchor.AcquireAsync(default);
        Assert.Equal(corrupt, h.H.Anchor.Read(held)!.Pending is not null);
    }

    [Fact]
    public async Task Stored_delta_and_archived_evidence_resolve_exact_bytes()
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot();
        await h.Accept(save);
        byte[] raw = SaveFixtures.Decompress(save.Packed);
        File.WriteAllBytes(h.H.Files.BasePath(1, 1), save.Packed);
        File.WriteAllBytes(h.H.Files.DeltaPath(1, 1, 1), SaveDelta.Compress(raw, raw));
        File.Delete(h.H.Files.FullPath(1, 1));
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
        string relative = Path.Combine("archive", "owned-proof");
        await new SaveArchive(h.H.Files).CopyOut([save.Version], Path.Combine(h.H.Data.Root, relative));
        h.H.Db.ArchivedColonies.Add(new ArchivedColony { PlayerId = 1, Dir = relative, State = ArchiveState.Archived });
        h.H.Db.SaveVersions.Remove(save.Version);
        await h.H.Db.SaveChangesAsync();
        h.H.Files.Delete(1, 1);
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Historical_snapshot_keeps_original_epoch_after_player_epoch_advances()
    {
        using var h = await SnapshotHarness.Create();
        await h.Accept(h.Snapshot());
        await h.H.Db.Players.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.SaveWriteEpoch, 3));
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Bootstrap_registry_is_bound_to_accepted_exact_snapshot()
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot(h.Registered());
        await h.Accept(save);
        var custody = Assert.Single(h.H.Db.PawnCustody.AsNoTracking());
        Assert.Equal(SnapshotHarness.Custody, custody.Id);
        Assert.Equal("Home", custody.LocationKind);
        Assert.Equal("Thing_Human1", custody.LocationId);
        Assert.Equal(Guid.ParseExact(save.Version.SnapshotId!, "N").ToString("D"), custody.FrontierTransitionId);
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Custody_without_registry_evidence_cannot_hide_behind_unrelated_snapshot()
    {
        using var h = await SnapshotHarness.Create();
        await h.Accept(h.Snapshot());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Change("Assign", (db, id) =>
        {
            db.PawnCustody.Add(new PawnCustodyRecord { Id = SnapshotHarness.Custody, WorldId = "world-1", PlayerId = 1,
                RestoreBoundary = h.Metadata().Scope.RestoreBoundary, LocationKind = "Home", LocationId = "Thing_Human1", FrontierTransitionId = id });
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public async Task Parcel_without_source_transition_cannot_hide_behind_unrelated_snapshot()
    {
        using var h = await SnapshotHarness.Create();
        await h.Accept(h.Snapshot());
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Change("Recall", (db, _) =>
        {
            db.OutpostParcelLinks.Add(new OutpostParcelLink { ParcelId = 10, WorldId = "world-1", PlayerId = 1,
                RestoreBoundary = h.Metadata().Scope.RestoreBoundary, TransitionId = Guid.NewGuid().ToString("D") });
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public async Task Known_registry_cannot_disappear_or_be_rebootstrapped()
    {
        using var h = await SnapshotHarness.Create();
        await h.Accept(h.Snapshot(h.Registered()));
        var missing = h.Snapshot();
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateHeldAsync(lease, 1,
            missing.Header, missing.Recovery.Outposts, missing.Version.RawSha256!, default));
    }

    [Fact]
    public async Task Tick_equality_does_not_prove_custody()
    {
        using var h = await SnapshotHarness.Create();
        var save = h.Snapshot(h.Registered());
        await h.Accept(save);
        await h.Change("Assign", (db, id) => db.PawnCustody.Where(p => p.Id == SnapshotHarness.Custody)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "Assigned").SetProperty(p => p.FrontierTransitionId, id)));
        var reappeared = h.Snapshot(save.Recovery.Outposts.Metadata.Clone()); // Same tick, new immutable snapshot.
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateHeldAsync(lease, 1,
            reappeared.Header, reappeared.Recovery.Outposts, reappeared.Version.RawSha256!, default));
    }

    [Fact]
    public async Task Pending_unmaterialized_parcel_does_not_require_future_receipt()
    {
        using var h = await SnapshotHarness.Create();
        var pending = await h.PendingReturn();
        await h.Accept(h.Snapshot(pending));
        Assert.Null(Assert.Single(h.H.Db.OutpostParcelLinks.AsNoTracking()).AcceptedSnapshotId);
        Assert.Equal("ReturnPending", Assert.Single(h.H.Db.PawnCustody.AsNoTracking()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Receipt_accepted_without_ack_moves_custody_once()
    {
        using var h = await SnapshotHarness.Create();
        var (metadata, attempt) = await h.Delivery();
        var save = h.Snapshot(metadata);
        await h.Accept(save);
        Assert.Equal("Home", Assert.Single(h.H.Db.PawnCustody.AsNoTracking()).LocationKind);
        Assert.Equal(save.Version.SnapshotId, Assert.Single(h.H.Db.OutpostParcelLinks.AsNoTracking()).AcceptedSnapshotId);
        var closed = Assert.Single(h.H.Db.OutpostDeliveryAttempts.AsNoTracking());
        Assert.False(closed.Active); Assert.Equal("Accepted", closed.State);
        Assert.NotNull(Assert.Single(h.H.Db.Parcels.AsNoTracking()).DeliveredAt);
        await h.Accept(h.Snapshot(metadata.Clone()));
        Assert.Equal(save.Version.SnapshotId, Assert.Single(h.H.Db.OutpostParcelLinks.AsNoTracking()).AcceptedSnapshotId);
        Assert.Single(h.H.Db.PawnCustody.AsNoTracking());
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Frontier_cannot_erase_delivery_or_reintroduce_assigned_pawn()
    {
        using var h = await SnapshotHarness.Create();
        var (metadata, _) = await h.Delivery();
        await h.Accept(h.Snapshot(metadata));
        var erased = metadata.Clone(); erased.Receipts.Clear(); erased.Frontier.Clear();
        var save = h.Snapshot(erased);
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateHeldAsync(lease, 1,
            save.Header, save.Recovery.Outposts, save.Version.RawSha256!, default));
    }

    [Fact]
    public async Task Accepted_delivery_receipt_survives_later_assignment_tombstone()
    {
        using var h = await SnapshotHarness.Create();
        var (metadata, _) = await h.Delivery();
        await h.Accept(h.Snapshot(metadata));
        await h.Change("Assign", (db, id) => db.PawnCustody.Where(p => p.Id == SnapshotHarness.Custody)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "Assigned").SetProperty(p => p.FrontierTransitionId, id)));
        metadata.Registry[0].Tombstone = true;
        metadata.Registry[0].PawnReferenceToken = "";
        await h.Accept(h.Snapshot(metadata));
        Assert.Equal("Assigned", Assert.Single(h.H.Db.PawnCustody.AsNoTracking()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await h.H.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Stored_link_requires_its_exact_snapshot_acceptance_transition()
    {
        using var h = await SnapshotHarness.Create();
        await h.Accept(h.Snapshot());
        string unrelated = await h.Change("Production", (_, _) => Task.CompletedTask);
        await h.H.Db.OutpostSnapshotLinks.ExecuteUpdateAsync(s => s.SetProperty(l => l.TransitionId, unrelated));
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateStoredEvidenceHeldAsync(lease, default));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("predecessor-proof")]
    [InlineData("attempt")]
    [InlineData("parcel")]
    public async Task Delivery_receipt_requires_exact_started_intent(string fault)
    {
        using var h = await SnapshotHarness.Create();
        var (metadata, _) = await h.Delivery();
        var receipt = metadata.Receipts.Single();
        switch (fault)
        {
            case "intent": receipt.IntentDigest = ByteString.CopyFrom(new byte[32]); break;
            case "predecessor-proof": receipt.Snapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "attempt": receipt.DeliveryAttemptId = Guid.NewGuid().ToString("D"); break;
            case "parcel": receipt.ParcelIds[0] = 999; break;
        }
        var save = h.Snapshot(metadata);
        await using var lease = await h.H.Authority.AcquireAsync([1], default);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Guard.ValidateHeldAsync(lease, 1,
            save.Header, save.Recovery.Outposts, save.Version.RawSha256!, default));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("boundary")]
    [InlineData("epoch")]
    [InlineData("frontier")]
    public async Task Stored_wrong_scope_boundary_epoch_or_frontier_keeps_maintenance(string fault)
    {
        using var h = await SnapshotHarness.Create();
        await h.Accept(h.Snapshot());
        var link = Assert.Single(h.H.Db.OutpostSnapshotLinks);
        if (fault == "scope") link.PlayerId = 2;
        if (fault == "boundary") link.RestoreBoundary = "wrong-boundary";
        if (fault == "epoch") link.SaveWriteEpoch = 77;
        if (fault == "frontier") link.FrontierDigest = new byte[32];
        // Mutating a protected projection must be rejected even before byte-level proof checks.
        if (fault == "scope") h.H.Db.ChangeTracker.Clear();
        else await h.H.Db.SaveChangesAsync();
        if (fault == "scope") await h.H.Db.OutpostSnapshotLinks.ExecuteUpdateAsync(s => s.SetProperty(l => l.RestoreBoundary, "other-owner"));
        Assert.Equal(OutpostStartupState.Maintenance, (await h.H.Authority.OpenAsync(default)).State);
    }
}

internal sealed record EvidenceSave(SaveVersion Version, SaveHeader Header, SaveRecoveryData Recovery, byte[] Packed);

internal sealed class SnapshotHarness : IDisposable
{
    public AuthorityHarness H { get; } = new();
    public OutpostSnapshotGuard Guard { get; }
    private int number;
    private SnapshotHarness() => Guard = new(H.Db, H.Files);
    public static async Task<SnapshotHarness> Create()
    {
        var h = new SnapshotHarness();
        h.H.Anchor.Provision();
        h.H.Db.World.Add(new Rimlink.Server.Storage.Entities.World { InfluenceInstanceId = "world-1" });
        h.H.Db.Accounts.Add(new Account { Id = 1, Player = new Player { Id = 1, Name = "Nova" } });
        await h.H.Db.SaveChangesAsync();
        await h.H.Authority.InitializeAsync(new("world-1", "admin", true), default);
        return h;
    }
    public OutpostSaveMetadata Metadata() => new()
    {
        SchemaVersion = 1,
        Scope = new() { WorldInstanceId = "world-1", OwnerPlayerId = 1,
            RestoreBoundary = H.Db.OutpostAuthority.AsNoTracking().Single().RestoreBoundary }
    };
    public EvidenceSave Snapshot(OutpostSaveMetadata? metadata = null)
    {
        string id = Guid.NewGuid().ToString("N");
        var section = new OutpostRecoveryData { Metadata = metadata ?? Metadata() };
        byte[] packed = RecoveryFixtures.Save(1000, id, generation: section.Metadata.Scope.SaveGeneration,
            epoch: section.Metadata.Scope.SaveWriteEpoch, outposts: section);
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(SaveFixtures.Decompress(packed)), out byte[] rawHash);
        var version = new SaveVersion { PlayerId = 1, Number = ++number, GameTick = 1000, Size = packed.Length,
            Sha256 = SHA256.HashData(packed), RawSha256 = rawHash, SnapshotId = id, RecoveryVersion = 1,
            SaveGeneration = recovery.Generation, SaveWriteEpoch = recovery.WriteEpoch,
            RecoveryJson = JsonSerializer.Serialize(recovery), CreatedAt = DateTime.UtcNow, IsCurrent = true };
        return new(version, RecoveryFixtures.Header(packed, 1000), recovery, packed);
    }
    public async Task Accept(EvidenceSave save)
    {
        var row = await H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        string id = Guid.ParseExact(save.Version.SnapshotId!, "N").ToString("D");
        await using var lease = await H.Authority.AcquireAsync([1], default);
        await Guard.ValidateHeldAsync(lease, 1, save.Header, save.Recovery.Outposts, save.Version.RawSha256!, default);
        H.Files.WriteAtomically(H.Files.FullPath(1, save.Version.Number), save.Packed);
        await H.Authority.CommitHeldAsync(lease, new(id, "world-1", 1, 0, row.RestoreBoundary,
            "SnapshotAccepted", "snapshot", new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)),
            ByteString.CopyFrom(save.Recovery.Outposts.Metadata.ToByteArray())), async (db, ct) =>
        {
            db.SaveVersions.Add(save.Version);
            await Guard.StageAcceptedHeldAsync(lease, save.Version, save.Recovery.Outposts, ct);
        }, default);
    }
    public const string Custody = "11111111-1111-1111-1111-111111111111";
    public const string Assignment = "22222222-2222-2222-2222-222222222222";
    public OutpostSaveMetadata Registered()
    {
        var metadata = Metadata();
        metadata.Registry.Add(new PawnRegistryEnvelope { SchemaVersion = 1, PawnCustodyId = Custody,
            Scope = metadata.Scope.Clone(), PawnReferenceToken = "Thing_Human1" });
        return metadata;
    }
    public async Task<string> Change(string kind, Func<Rimlink.Server.Storage.RimlinkDbContext, string, Task> stage)
    {
        string id = Guid.NewGuid().ToString("D");
        var row = await H.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        await using var lease = await H.Authority.AcquireAsync([1], default);
        await H.Authority.CommitHeldAsync(lease, new(id, "world-1", 1, 0, row.RestoreBoundary, kind, "fixture",
            new(row.HeadId, ByteString.CopyFrom(row.HeadDigest)), ByteString.CopyFromUtf8(kind)), (db, _) => stage(db, id), default);
        return id;
    }
    public async Task<OutpostSaveMetadata> PendingReturn()
    {
        var metadata = Registered();
        await Accept(Snapshot(metadata));
        metadata.Registry[0].Tombstone = true; metadata.Registry[0].PawnReferenceToken = "";
        metadata.Registry[0].AssignmentId = Assignment;
        await Change("Recall", async (db, id) =>
        {
            var payload = new OutpostParcelPayload { SchemaVersion = 1, Kind = OutpostPayloadKind.Workers,
                Scope = metadata.Scope.Clone(), PawnCustodyIds = { Custody }, AssignmentIds = { Assignment } };
            byte[] bytes = payload.ToByteArray();
            db.Parcels.Add(new Rimlink.Server.Storage.Entities.Parcel { Id = 10, ToPlayerId = 1, CreatedAt = DateTime.UtcNow });
            db.OutpostParcelLinks.Add(new OutpostParcelLink { ParcelId = 10, PlayerId = 1, WorldId = "world-1",
                RestoreBoundary = metadata.Scope.RestoreBoundary, BatchId = Guid.NewGuid().ToString("D"), Payload = bytes,
                PayloadDigest = SHA256.HashData(bytes), TransitionId = id });
            await db.PawnCustody.Where(p => p.Id == Custody).ExecuteUpdateAsync(s => s.SetProperty(p => p.LocationKind, "ReturnPending")
                .SetProperty(p => p.LocationId, "10").SetProperty(p => p.AssignmentId, Assignment).SetProperty(p => p.FrontierTransitionId, id));
        });
        return metadata;
    }
    public async Task<(OutpostSaveMetadata Metadata, OutpostDeliveryAttempt Attempt)> Delivery()
    {
        var metadata = await PendingReturn();
        var canonical = Snapshot(metadata); await Accept(canonical);
        var attempt = new OutpostDeliveryAttempt { Id = Guid.NewGuid().ToString("D"), WorldId = "world-1", PlayerId = 1,
            RestoreBoundary = metadata.Scope.RestoreBoundary, ParcelId = 10, RequestId = Guid.NewGuid().ToString("N"),
            IntentDigest = SHA256.HashData("delivery-intent"u8), PayloadDigest = Assert.Single(H.Db.OutpostParcelLinks.AsNoTracking()).PayloadDigest,
            CanonicalSnapshotId = canonical.Version.SnapshotId!, CanonicalSnapshotHash = canonical.Version.RawSha256!, State = "Started", Active = true };
        await Change("BeginDelivery", (db, id) => { attempt.TransitionId = id; db.OutpostDeliveryAttempts.Add(attempt); return Task.CompletedTask; });
        var entry = await H.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
        metadata.Registry[0].Tombstone = false; metadata.Registry[0].PawnReferenceToken = "Thing_Human55";
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = entry.Id, PredecessorId = entry.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(entry.Digest), ClientRequestId = attempt.RequestId, IntentDigest = ByteString.CopyFrom(attempt.IntentDigest),
            Snapshot = new OutpostSnapshotProof { Scope = metadata.Scope.Clone(), SnapshotId = attempt.CanonicalSnapshotId,
                RawSha256 = ByteString.CopyFrom(attempt.CanonicalSnapshotHash) }, PawnCustodyIds = { Custody }, AssignmentIds = { Assignment },
            ParcelIds = { 10 }, DeliveryAttemptId = attempt.Id });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = entry.Id, PredecessorId = entry.PredecessorId ?? "", TransitionDigest = ByteString.CopyFrom(entry.Digest) });
        return (metadata, attempt);
    }
    public void Dispose() => H.Dispose();
}
