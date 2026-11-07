using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed partial class OrdinaryReturnTests
{
    [Fact]
    public async Task Negative_decision_starts_one_local_return_and_fences_the_late_original_post()
    {
        using var h = await CustodyHarness.Returned();
        var original = Post(h); var cancel = await Removed(h, original);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        long parcels = await h.Db.Parcels.CountAsync();
        Assert.Equal(OrdinaryOperationStatus.Unknown, (await returns.ResolveAsync(1, new() { Operation = cancel }, default)).Status);
        var started = await returns.BeginReturnAsync(1, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, started.Status);
        Assert.Equal(cancel, started.ReturnProof.Authorization);
        Assert.Equal(original.Pawn, Assert.Single(started.ReturnProof.Payload.Pawns.Pawns));
        Assert.Empty(started.ReturnProof.Payload.Items.Items);
        Assert.True(Guid.TryParseExact(started.ReturnProof.TransitionId, "D", out _));
        Assert.Equal(32, started.ReturnProof.TransitionDigest.Length);
        Assert.Equal(32, started.ReturnProof.PayloadDigest.Length);
        Assert.Equal(started, await returns.BeginReturnAsync(1, cancel, default));
        Assert.Equal(started, await returns.ResolveAsync(1, new() { Operation = cancel }, default));
        var row = await h.Row(); Assert.Equal("LocalReturnPending", row.LocationKind); Assert.True(row.Tombstone);
        Assert.Equal(cancel.ReturnAttemptId, row.LocationId);
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<MarketService>().Post(1, original));
        Assert.Empty(await h.Db.Lots.ToListAsync()); Assert.Equal(parcels, await h.Db.Parcels.CountAsync());
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_committed_post_remains_applied_after_withdrawal_not_a_missing_source(bool withdrawn)
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var market = scope.ServiceProvider.GetRequiredService<MarketService>();
        var lot = await market.Post(1, original);
        if (withdrawn) await market.Withdraw(1, lot.Id);
        var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        var applied = await returns.BeginReturnAsync(1, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.Applied, applied.Status); Assert.Equal(lot.Id, applied.SourceId); Assert.Null(applied.ReturnProof);
        Assert.Equal(applied, await returns.ResolveAsync(1, new() { Operation = cancel }, default));
        Assert.Equal(withdrawn ? "ReturnPending" : "Escrow", (await h.Row()).LocationKind);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Mixed_send_return_keeps_all_physical_payload_without_charging_or_materializing_wallet_silver()
    {
        using var h = await CustodyHarness.Returned();
        var original = new TransferRequest { ClientRequestId = Guid.NewGuid().ToString("N"), ToPlayerId = 2, GameTick = 1000,
            Silver = 25, Items = new() { Items = { EconomyFixtures.Item("Steel", 13) } }, Pawns = new() { Pawns = { h.Blob.Clone() } } };
        var cancel = await Removed(h, original);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        long balance = (await h.Db.Wallets.AsNoTracking().SingleAsync(w => w.PlayerId == 1)).Balance;
        var started = await scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnStarted, started.Status);
        Assert.Equal(original.Items, started.ReturnProof.Payload.Items); Assert.Equal(original.Pawns, started.ReturnProof.Payload.Pawns);
        Assert.Equal(balance, (await h.Db.Wallets.AsNoTracking().SingleAsync(w => w.PlayerId == 1)).Balance);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("intent")][InlineData("owner")][InlineData("local-id")][InlineData("removal-hash")][InlineData("canonical-hash")]
    public async Task Changed_identity_or_unaccepted_proof_never_installs_a_negative_decision(string fault)
    {
        using var h = await CustodyHarness.Returned(); var cancel = await Removed(h, Post(h));
        switch (fault)
        {
            case "intent": cancel.OriginalRequest = ByteString.CopyFromUtf8("not the accepted request"); break;
            case "owner": cancel.Scope.OwnerPlayerId = 2; break;
            case "local-id": cancel.Capture.LocalOperationId = Guid.NewGuid().ToString("N"); break;
            case "removal-hash": cancel.RemovalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "canonical-hash": cancel.CanonicalSnapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
        }
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var before = (await h.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId;
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default));
        Assert.Equal(before, (await h.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal("Home", (await h.Row()).LocationKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_fenced_key_cannot_be_replayed_with_stripped_pawns_or_changed_payload(bool emptyPawns)
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default);
        var changed = original.Clone(); changed.Price++;
        if (emptyPawns) { changed.Pawn = null; changed.Items = new() { Items = { EconomyFixtures.Item("Steel", 3) } }; }
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<MarketService>().Post(1, changed));
        Assert.Empty(await h.Db.Lots.ToListAsync());
    }

    [Fact]
    public async Task A_negative_transition_without_its_exact_projection_is_rejected_before_external_commit()
    {
        using var h = await CustodyHarness.Returned(); var cancel = await Removed(h, Post(h));
        var head = await h.Db.OutpostAuthority.AsNoTracking().SingleAsync();
        await using (var lease = await h.Authority.AcquireAsync([1], default))
            await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.Authority.CommitHeldAsync(lease,
                new(cancel.ReturnAttemptId, cancel.Scope.WorldInstanceId, 1, cancel.Scope.SaveGeneration, cancel.Scope.RestoreBoundary,
                    "OrdinaryReturnStarted", "player:1", new(head.HeadId, ByteString.CopyFrom(head.HeadDigest)), cancel.ToByteString()),
                (_, _) => Task.CompletedTask, default));
        Assert.Equal(head.HeadId, (await h.Db.OutpostAuthority.AsNoTracking().SingleAsync()).HeadId);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Fact]
    public async Task Exact_returned_snapshot_imports_once_and_retains_the_negative_fence_after_later_death()
    {
        using var h = await CustodyHarness.Returned(); var original = Post(h); var cancel = await Removed(h, original);
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var returns = scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>();
        var started = await returns.BeginReturnAsync(1, cancel, default);
        var home = await Returned(h, cancel, started); await h.F.F.H.Accept(home);
        var row = await h.Row(); long revision = row.Revision;
        Assert.Equal("Home", row.LocationKind); Assert.False(row.Tombstone); Assert.Equal("Thing_HumanLocalReturn", row.LocationId);
        var accepted = await returns.BeginReturnAsync(1, cancel, default);
        Assert.Equal(OrdinaryOperationStatus.ReturnAccepted, accepted.Status); Assert.Equal(started.ReturnProof, accepted.ReturnProof);
        var repeated = h.F.F.H.Snapshot(home.Recovery.Outposts.Metadata.Clone(), tick: 1002,
            removed: [cancel.Capture.LocalOperationId], returned: [cancel.Capture.LocalOperationId]);
        await h.F.F.H.Accept(repeated);
        Assert.Equal(revision, (await h.Row()).Revision);
        Assert.Equal(home.Version.SnapshotId, (await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()).AcceptedSnapshotId);
        var dead = home.Recovery.Outposts.Metadata.Clone(); dead.Registry.Single(r => r.PawnCustodyId == SnapshotHarness.Custody).Tombstone = true;
        await h.F.F.H.Accept(h.F.F.H.Snapshot(dead, tick: 1003, removed: [cancel.Capture.LocalOperationId], returned: [cancel.Capture.LocalOperationId]));
        Assert.True((await h.Row()).Tombstone);
        await Assert.ThrowsAsync<RpcException>(() => scope.ServiceProvider.GetRequiredService<MarketService>().Post(1, original));
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    [Theory]
    [InlineData("local-id")][InlineData("attempt")][InlineData("snapshot")][InlineData("custody")][InlineData("assignment")][InlineData("missing-returned")]
    public async Task Wrong_or_incomplete_local_return_receipt_cannot_revive_the_server_held_pawn(string fault)
    {
        using var h = await CustodyHarness.Returned(); var cancel = await Removed(h, Post(h));
        using var host = await h.StartHostAsync(); using var scope = host.Services.CreateScope();
        var started = await scope.ServiceProvider.GetRequiredService<OrdinaryReturnService>().BeginReturnAsync(1, cancel, default);
        var prepared = await Returned(h, cancel, started, fault != "missing-returned");
        var metadata = prepared.Recovery.Outposts.Metadata.Clone();
        var receipt = metadata.Receipts.Single(r => r.OrdinaryReturnAttemptId.Length > 0);
        switch (fault)
        {
            case "local-id": receipt.OrdinaryLocalOperationId = Guid.NewGuid().ToString("N"); break;
            case "attempt": receipt.OrdinaryReturnAttemptId = Guid.NewGuid().ToString("D"); break;
            case "snapshot": receipt.Snapshot.RawSha256 = ByteString.CopyFrom(new byte[32]); break;
            case "custody": receipt.PawnCustodyIds.Clear(); break;
            case "assignment": receipt.AssignmentIds.Clear(); break;
        }
        var forged = fault == "missing-returned" ? prepared : h.F.F.H.Snapshot(metadata, removed: [cancel.Capture.LocalOperationId], returned: [cancel.Capture.LocalOperationId]);
        await Assert.ThrowsAsync<OutpostEvidenceUnavailableException>(() => h.F.F.H.Accept(forged));
        Assert.Equal("LocalReturnPending", (await h.Row()).LocationKind); Assert.True((await h.Row()).Tombstone);
        Assert.Equal("Started", (await h.Db.OutpostOrdinaryReturns.AsNoTracking().SingleAsync()).State);
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }

    internal static async Task<EvidenceSave> Returned(CustodyHarness h, OrdinaryCancelRequest cancel, OrdinaryOperationReply started, bool returned = true)
    {
        var removed = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.SnapshotId == cancel.RemovalSnapshot.SnapshotId);
        var metadata = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(removed).Outposts.Metadata.Clone();
        foreach (var pawn in cancel.Capture.Pawns)
        {
            var registry = metadata.Registry.Single(r => r.PawnCustodyId == pawn.Pawn.Registry.PawnCustodyId);
            registry.Tombstone = false; registry.PawnReferenceToken = "Thing_HumanLocalReturn";
        }
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = started.ReturnProof.TransitionId,
            PredecessorId = started.ReturnProof.PredecessorId, TransitionDigest = started.ReturnProof.TransitionDigest,
            ClientRequestId = cancel.Capture.ClientRequestId, IntentDigest = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(cancel.ToByteArray())),
            Snapshot = cancel.CanonicalSnapshot.Clone(), OrdinaryReturnAttemptId = cancel.ReturnAttemptId,
            OrdinaryLocalOperationId = cancel.Capture.LocalOperationId,
            PawnCustodyIds = { cancel.Capture.Pawns.Select(p => p.Pawn.Registry.PawnCustodyId) },
            AssignmentIds = { cancel.Capture.Pawns.Select(p => p.Pawn.Registry.AssignmentId).Where(id => id.Length > 0).Distinct() } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = started.ReturnProof.TransitionId,
            TransitionDigest = started.ReturnProof.TransitionDigest, PredecessorId = started.ReturnProof.PredecessorId });
        return h.F.F.H.Snapshot(metadata, tick: 1001, removed: [cancel.Capture.LocalOperationId], returned: returned ? [cancel.Capture.LocalOperationId] : []);
    }

    private static PostRequest Post(CustodyHarness h) => new() { ClientRequestId = Guid.NewGuid().ToString("N"),
        Pawn = h.Blob.Clone(), Price = 150, GameTick = 1000 };

    private static async Task<OrdinaryCancelRequest> Removed(CustodyHarness h, IMessage original)
    {
        var evidence = await h.Depart(original);
        return new() { Scope = evidence.RemovalSnapshot.Scope.Clone(), Capture = evidence.OrdinaryCapture.Clone(),
            OriginalRequest = original.ToByteString(), RemovalSnapshot = evidence.RemovalSnapshot.Clone(),
            CanonicalSnapshot = evidence.RemovalSnapshot.Clone(), ReturnAttemptId = Guid.NewGuid().ToString("D") };
    }
}
