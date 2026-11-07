using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Economy;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Support;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostCustodyRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assign_vs_ordinary_send_has_one_winner(bool sendFirst)
    {
        using var h = await CustodyHarness.Returned(); h.AddRecipient(1); await h.Db.SaveChangesAsync();
        var recipient = new OutpostSaveMetadata { SchemaVersion = 1, Scope = h.F.Scope.Clone() }; recipient.Scope.OwnerPlayerId = 2;
        await h.F.F.H.Accept(h.F.F.H.Snapshot(recipient));
        await h.Db.Wallets.Where(w => w.PlayerId == 1).ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, 1000));
        var metadata = h.HomeSave.Recovery.Outposts.Metadata.Clone();
        var worker = metadata.WorkerCaptures.Single().Clone(); worker.AssignmentId = Guid.NewGuid().ToString("D");
        worker.BlobSha256 = ByteString.CopyFrom(SHA256.HashData(worker.Pawn.ToByteArray()));
        var prepare = h.F.F.Request.Clone(); prepare.ClientRequestId = Guid.NewGuid().ToString("N"); prepare.OutpostId = Guid.NewGuid().ToString("D"); prepare.ExpectedRevision = 0;
        prepare.Workers.Clear(); prepare.Workers.Add(worker);
        prepare.Caravan.ClientRequestId = prepare.ClientRequestId; prepare.Caravan.CaravanId = "Caravan_Race";
        prepare.Caravan.GameBranchId = Guid.NewGuid().ToString("D"); prepare.Caravan.RosterReferenceTokens.Clear();
        prepare.Caravan.RosterReferenceTokens.Add(worker.Pawn.Registry.PawnReferenceToken);
        metadata.WorkerCaptures.Clear(); metadata.WorkerCaptures.Add(worker); metadata.Caravans.Clear(); metadata.Caravans.Add(prepare.Caravan.Clone());
        var pre = h.F.F.H.Snapshot(metadata); await h.F.F.H.Accept(pre);
        prepare.PreRemoval = AssignmentHarness.SnapshotProof(pre); prepare.IntentDigest = OutpostRequestIdentity.Digest(prepare);
        string sendId = Guid.NewGuid().ToString("N"), localId = Guid.NewGuid().ToString("N");
        var send = new TransferRequest { ClientRequestId = sendId, ToPlayerId = 2, Items = new(), Pawns = new() { Pawns = { h.Blob.Clone() } }, GameTick = 1100 };
        var removed = metadata.Clone(); removed.Registry[0].Tombstone = true;
        removed.OrdinaryOutgoing.Add(OrdinaryCapture.Create(send, localId, metadata.Scope));
        var post = h.F.F.H.Snapshot(removed, removed: [localId]);
        using var host = await h.StartHostAsync(); using var assignmentScope = host.Services.CreateScope(); using var sendScope = host.Services.CreateScope();
        var assignment = assignmentScope.ServiceProvider.GetRequiredService<OutpostAssignmentService>();
        var transfer = sendScope.ServiceProvider.GetRequiredService<TransferService>();
        async Task<bool> Assign()
        {
            try { return (await assignment.PrepareAsync(1, prepare, default)).Status == OutpostOperationStatus.Prepared; }
            catch (global::Grpc.Core.RpcException error)
            { Assert.Contains(error.ErrorDetail()?.Code, new ErrorCode?[] { ErrorCode.OutpostConflict, ErrorCode.RecoveryRequired }); return false; }
        }
        async Task<bool> Send()
        {
            try
            {
                await h.F.F.H.Accept(post);
                await transfer.Send(1, send);
                return true;
            }
            catch (OutpostEvidenceUnavailableException) { return false; }
            catch (global::Grpc.Core.RpcException error)
            { Assert.Contains(error.ErrorDetail()?.Code, new ErrorCode?[] { ErrorCode.OutpostConflict, ErrorCode.RecoveryRequired }); return false; }
        }
        var held = await h.Authority.AcquireAsync([1, 2], default);
        var first = sendFirst ? Send() : Assign(); var second = sendFirst ? Assign() : Send();
        await held.DisposeAsync(); var results = await Task.WhenAll(first, second);
        Assert.Single(results, won => won);
        var row = await h.Row(); Assert.Equal(1, row.PlayerId);
        Assert.Contains(row.LocationKind, new[] { "Prepared", "TransferPending" });
        Assert.Equal(row.LocationKind == "Prepared" ? 1 : 0, await h.Db.OutpostAssignments.CountAsync(a => a.Active && a.PawnCustodyId == SnapshotHarness.Custody));
        Assert.Equal(row.LocationKind == "TransferPending" ? 1 : 0, await h.Db.Parcels.CountAsync(p => p.ToPlayerId == 2));
        Assert.Equal(OutpostStartupState.Ready, (await h.Authority.OpenAsync(default)).State);
    }
}
