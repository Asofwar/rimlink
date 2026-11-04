using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rimlink.Protocol.V1;
using Rimlink.Server.Application.Auth;
using Rimlink.Server.Application.Outposts;
using Rimlink.Server.Tests.Outposts;

namespace Rimlink.Server.Tests.Support;

/// <summary>Real retained custody, exact saved bytes, authority anchor and RPC host, owned by one client test.</summary>
public sealed class OutpostClientFixture : IDisposable
{
    private readonly MutationHarness harness;
    public ServerFixture Server { get; }
    public string LoginName => "outpost-client";
    public OutpostScopeMessage Scope => harness.Scope.Clone();
    public OutpostProofRequest CommitRequest => harness.F.Proof(harness.Post);
    public OutpostPrepareRequest PrepareRequest => harness.F.Request.Clone();
    public OutpostMutationRequest CloseRequest => harness.Request(OutpostMutationKind.Close);
    public byte[] RawSave => SaveFixtures.Decompress(harness.Post.Packed);
    private OutpostClientFixture(MutationHarness harness, ServerFixture server) { this.harness = harness; Server = server; }
    public static async Task<OutpostClientFixture> Create(bool admin = false, bool prepareOnly = false)
    {
        return await Open(await MutationHarness.Create(prepareOnly: prepareOnly), admin);
    }
    private static async Task<OutpostClientFixture> Open(MutationHarness harness, bool admin)
    {
        try
        {
            var account = await harness.Db.Accounts.SingleAsync(); account.Login = "outpost-client"; account.PasswordHash = PasswordHasher.Hash("pw");
            if (admin) account.Role = Rimlink.Server.Storage.Entities.Role.Admin;
            await harness.Db.SaveChangesAsync(); var shared = harness.F.H.H;
            var server = HygieneFixtures.HygieneServer().WithoutWorld().WithDataDir(shared.Data.Root).KeepDataDir()
                .WithSetting("Outposts:AnchorDirectory", Path.Combine(shared.Root, "external"))
                .WithSetting("Outposts:StorageContract", "sqlite-local-v1")
                .WithServices(s => { s.AddSingleton(shared.Files); s.AddSingleton(shared.Owners); s.AddSingleton(shared.Anchor); });
            try
            {
                _ = server.Services;
                // Fixture-only advertisement; production remains OFF until Task15's complete safety graph.
                server.Services.GetRequiredService<OutpostFeature>().IsAvailable = true;
                return new(harness, server);
            }
            catch { server.Dispose(); throw; }
        }
        catch { harness.Dispose(); throw; }
    }
    public static async Task<(OutpostClientFixture Fixture, OrdinaryCancelRequest Request)> CreateOrdinary()
    {
        var custody = await CustodyHarness.Returned(); CustodyTransferEvidence departure; TransferRequest original;
        try
        {
            original = new TransferRequest { ToPlayerId = 2, Silver = 25, GameTick = 1000, ClientRequestId = Guid.NewGuid().ToString("N"),
                Items = new() { Items = { EconomyFixtures.Item("Steel", 13) } }, Pawns = new() { Pawns = { custody.Blob.Clone() } } };
            departure = await custody.Depart(original);
        }
        catch { custody.Dispose(); throw; }
        var fixture = await Open(custody.F, false);
        return (fixture, new() { Scope = fixture.Scope, Capture = departure.OrdinaryCapture.Clone(), OriginalRequest = original.ToByteString(),
            RemovalSnapshot = departure.RemovalSnapshot.Clone(), CanonicalSnapshot = departure.RemovalSnapshot.Clone(), ReturnAttemptId = Guid.NewGuid().ToString("D") });
    }
    public async Task<byte[]> AcceptOrdinaryReturn(OrdinaryCancelRequest request, OrdinaryOperationReply reply)
    {
        var removed = await harness.Db.SaveVersions.AsNoTracking().SingleAsync(v => v.SnapshotId == request.RemovalSnapshot.SnapshotId);
        var metadata = Rimlink.Server.Application.Saves.SaveStore.RecoveryOf(removed).Outposts.Metadata.Clone();
        foreach (var pawn in request.Capture.Pawns)
        {
            var registry = metadata.Registry.Single(r => r.PawnCustodyId == pawn.Pawn.Registry.PawnCustodyId);
            registry.Tombstone = false; registry.PawnReferenceToken = "Thing_HumanLocalReturn";
        }
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = reply.ReturnProof.TransitionId,
            PredecessorId = reply.ReturnProof.PredecessorId, TransitionDigest = reply.ReturnProof.TransitionDigest,
            ClientRequestId = request.Capture.ClientRequestId, IntentDigest = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(request.ToByteArray())),
            Snapshot = request.CanonicalSnapshot.Clone(), OrdinaryReturnAttemptId = request.ReturnAttemptId, OrdinaryLocalOperationId = request.Capture.LocalOperationId,
            PawnCustodyIds = { request.Capture.Pawns.Select(p => p.Pawn.Registry.PawnCustodyId) },
            AssignmentIds = { request.Capture.Pawns.Select(p => p.Pawn.Registry.AssignmentId).Where(id => id.Length > 0).Distinct() } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = reply.ReturnProof.TransitionId,
            TransitionDigest = reply.ReturnProof.TransitionDigest, PredecessorId = reply.ReturnProof.PredecessorId });
        var saved = harness.F.H.Snapshot(metadata, tick: 1001, removed: [request.Capture.LocalOperationId], returned: [request.Capture.LocalOperationId]);
        await harness.F.H.Accept(saved); return SaveFixtures.Decompress(saved.Packed);
    }
    public async Task AddOrdinaryRecipientAccount()
    {
        var player = new Rimlink.Server.Storage.Entities.Player { Id = 2, Name = "Receiver" };
        harness.Db.Accounts.Add(new Rimlink.Server.Storage.Entities.Account { Login = "ordinary-recipient",
            PasswordHash = PasswordHasher.Hash("pw"), Player = player });
        await harness.Db.SaveChangesAsync();
        var target = new OutpostSaveMetadata { SchemaVersion = 1, Scope = Scope };
        target.Scope.OwnerPlayerId = player.Id; target.Scope.SaveGeneration = player.SaveGeneration; target.Scope.SaveWriteEpoch = player.SaveWriteEpoch;
        await harness.F.H.Accept(harness.F.H.Snapshot(target));
    }
    public void SetAvailable(bool value) => Server.Services.GetRequiredService<OutpostFeature>().IsAvailable = value;
    public async Task<OutpostDeliveryRequest> CloseForDelivery()
    {
        var result = await harness.Service.MutateAsync(1, harness.Request(OutpostMutationKind.Close), default);
        long id = result.ParcelIds.Single(); var link = await harness.Db.OutpostParcelLinks.AsNoTracking().SingleAsync(p => p.ParcelId == id);
        return new() { Scope = Scope, ClientRequestId = Guid.NewGuid().ToString("N"), ParcelId = id,
            PayloadDigest = ByteString.CopyFrom(link.PayloadDigest), CanonicalSnapshot = AssignmentHarness.SnapshotProof(harness.Post) };
    }
    public async Task<byte[]> AcceptDelivery(OutpostDeliveryRequest request, OutpostDeliveryReply reply)
    {
        var attempt = await harness.Db.OutpostDeliveryAttempts.AsNoTracking().SingleAsync(a => a.Id == reply.AttemptId);
        var transition = await harness.Db.OutpostTransitions.AsNoTracking().SingleAsync(t => t.Id == attempt.TransitionId);
        var metadata = harness.Post.Recovery.Outposts.Metadata.Clone(); metadata.Registry[0].Tombstone = false;
        metadata.Registry[0].PawnReferenceToken = "Thing_HumanReturned";
        metadata.Receipts.Add(new OutpostTransitionReceipt { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest), ClientRequestId = request.ClientRequestId,
            IntentDigest = ByteString.CopyFrom(attempt.IntentDigest), Snapshot = request.CanonicalSnapshot.Clone(),
            DeliveryAttemptId = reply.AttemptId, ParcelIds = { request.ParcelId },
            PawnCustodyIds = { harness.Workers.Select(w => w.PawnCustodyId) }, AssignmentIds = { harness.Workers.Select(w => w.AssignmentId) } });
        metadata.Frontier.Add(new OutpostFrontierEntry { TransitionId = transition.Id, PredecessorId = transition.PredecessorId ?? "",
            TransitionDigest = ByteString.CopyFrom(transition.Digest) });
        var saved = harness.F.H.Snapshot(metadata); await harness.F.H.Accept(saved); return SaveFixtures.Decompress(saved.Packed);
    }
    public void Dispose() { Server.Dispose(); harness.Dispose(); }
}
