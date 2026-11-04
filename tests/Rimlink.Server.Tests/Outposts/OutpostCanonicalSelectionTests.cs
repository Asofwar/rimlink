using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Server.Application.Economy;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Outposts;
using Xunit;
using Rimlink.Protocol;
using Rimlink.Server.Tests.Support;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostCanonicalSelectionTests
{
    [Fact]
    public async Task Admission_current_proof_uses_selected_snapshot_not_largest_number()
    {
        using var f = await AssignmentHarness.Create();
        var selected = f.Pre;
        var later = f.H.Snapshot(selected.Recovery.Outposts.Metadata.Clone());
        await f.H.Accept(later);
        await Select(f.Db, f.H.H.Authority, selected.Version.Id);
        var actual = await f.Service.ProofAsync(f.Request.Scope, AssignmentHarness.SnapshotProof(selected), current: true, default);
        Assert.Equal(selected.Recovery.Outposts.Metadata, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_custody_reads_the_selected_exact_capture_not_largest_number(bool entry)
    {
        using var h = await CustodyHarness.Returned();
        var request = new PostRequest { Pawn = h.Blob.Clone(), Price = 150, GameTick = 1000, ClientRequestId = Guid.NewGuid().ToString("N") };
        var evidence = await h.Depart(request);
        var selected = await h.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.SnapshotId == evidence.RemovalSnapshot.SnapshotId);
        var metadata = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(selected).Outposts.Metadata.Clone();
        var later = h.F.F.H.Snapshot(metadata, removed: [evidence.OrdinaryCapture.LocalOperationId]);
        await h.F.F.H.Accept(later);
        await Select(h.Db, h.Authority, selected.Id);
        if (entry)
        {
            using var host = h.StartHost(); using var services = host.Services.CreateScope();
            var lot = await services.ServiceProvider.GetRequiredService<MarketService>().Post(1, request);
            var custody = await h.Row();
            var transition = await h.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == custody.FrontierTransitionId);
            var actual = CustodyTransferEvidence.Parser.ParseFrom(transition.Intent);
            Assert.Equal(evidence.RemovalSnapshot, actual.RemovalSnapshot);
            Assert.Equal("market:" + lot.Id, custody.LocationId);
        }
        else
        {
            await using var lease = await h.Authority.AcquireAsync([1], default);
            await h.Bridge.ValidateOutgoingHeldAsync(lease, 1, evidence.Pawns, evidence, default);
        }
        if (!entry) Assert.Empty(await h.Db.Lots.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Offline_recipient_compatibility_uses_selected_save_for_outgoing_and_escrow(bool escrow, bool compatible)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1);
        h.Db.Wallets.Add(new Rimlink.Server.Storage.Entities.Wallet { PlayerId = 2, Balance = 1000 });
        await h.Db.SaveChangesAsync();
        h.Sessions.End(h.Sessions.ByPlayer(2)!.Token); // Before host subscriptions: a genuinely offline recipient.
        var target = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; target.Scope.OwnerPlayerId = 2;
        var baseline = h.F.F.H.Snapshot(target); await h.F.F.H.Accept(baseline);
        string id = Guid.NewGuid().ToString("N");
        byte[] packed = RecoveryFixtures.Save(1000, id);
        var recovery = SaveRecoveryData.ReadXml(new MemoryStream(SaveFixtures.Decompress(packed)), out var rawHash);
        var legacy = new Rimlink.Server.Storage.Entities.SaveVersion { PlayerId = 2, Number = baseline.Version.Number + (compatible ? 1000 : -1),
            GameTick = 1000, Size = packed.Length, Sha256 = SHA256.HashData(packed), RawSha256 = rawHash, SnapshotId = id,
            RecoveryVersion = recovery.Version, RecoveryJson = JsonSerializer.Serialize(recovery), CreatedAt = DateTime.UtcNow };
        await using (var lease = await h.Authority.AcquireAsync([2], default))
        {
            h.F.F.H.H.Files.WriteAtomically(h.F.F.H.H.Files.FullPath(2, legacy.Number), packed);
            h.Db.SaveVersions.Add(legacy); await h.Db.SaveChangesAsync();
            await h.Db.SaveVersions.Where(v => v.PlayerId == 2).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false));
            long selected = compatible ? baseline.Version.Id : legacy.Id;
            await h.Db.SaveVersions.Where(v => v.Id == selected).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, true));
        }
        IMessage request = escrow
            ? new PostRequest { Pawn = h.Blob.Clone(), Price = 150, GameTick = 1000, ClientRequestId = Guid.NewGuid().ToString("N") }
            : new TransferRequest { ToPlayerId = 2, Items = new(), Pawns = new() { Pawns = { h.Blob.Clone() } }, GameTick = 1000, ClientRequestId = Guid.NewGuid().ToString("N") };
        await h.Depart(request);
        using var host = h.StartHost(); using var scope = host.Services.CreateScope();
        Func<Task> deliver;
        if (escrow)
        {
            var market = scope.ServiceProvider.GetRequiredService<MarketService>();
            var lot = await market.Post(1, (PostRequest)request);
            deliver = async () => { await market.Buy(2, lot.Id); };
        }
        else deliver = async () => { await scope.ServiceProvider.GetRequiredService<TransferService>().Send(1, (TransferRequest)request); };
        if (compatible) { await deliver(); Assert.Equal("TransferPending", (await h.Row()).LocationKind); }
        else
        {
            var error = await Assert.ThrowsAsync<global::Grpc.Core.RpcException>(deliver);
            Assert.Equal("custody-recipient-client-required", error.Status.Detail);
            Assert.Empty(await h.Db.OutpostParcelLinks.AsNoTracking().Where(l => l.PlayerId == 2).ToListAsync());
        }
    }

    // This read-selector fixture deliberately selects an older, already accepted row under the real authority fence.
    // Epoch advancement and authenticated rollback provenance are exercised by OutpostRollbackTests/BridgeTests.
    private static async Task Select(Rimlink.Server.Storage.RimlinkDbContext db, OutpostAuthority authority, long id)
    {
        await using var lease = await authority.AcquireAsync([1], default);
        await db.SaveVersions.Where(v => v.PlayerId == 1).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false));
        await db.SaveVersions.Where(v => v.Id == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, true));
    }
}
