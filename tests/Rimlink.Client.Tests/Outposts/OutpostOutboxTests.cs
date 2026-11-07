using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OutpostOutboxTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-outpost-outbox", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "outbox.json");
    private TransferOutbox Open() => new(PathName, new ConsoleLog(), 3, 4);
    internal static IMessage Request(string kind)
    {
        var scope = OutpostReconciliationTests.Scope(); var proof = OutpostReconciliationTests.Proof();
        const string id = "22222222222222222222222222222222"; var digest = ByteString.CopyFrom(new byte[32]);
        return kind switch
        {
            "prepare" => new OutpostPrepareRequest { Scope = scope, ClientRequestId = id, IntentDigest = digest, PreRemoval = proof },
            "commit" => new OutpostProofRequest { Scope = scope, ClientRequestId = id, IntentDigest = digest, PostRemoval = proof },
            "cancel" => new OutpostCancelRequest { Scope = scope, ClientRequestId = id, IntentDigest = digest, CancellationProof = proof },
            "mutate" => new OutpostMutationRequest { Scope = scope, ClientRequestId = id, IntentDigest = digest, CanonicalSnapshot = proof },
            "delivery" => new OutpostDeliveryRequest { Scope = scope, ClientRequestId = id, ParcelId = 9, PayloadDigest = digest, CanonicalSnapshot = proof },
            _ => throw new ArgumentException(kind),
        };
    }
    internal static SaveMeta Meta(byte hash = 9) => new("seed", "Nova", 1000, SaveGeneration: 3,
        Recovery: new SaveRecoveryData { Version = 1, Generation = 3, WriteEpoch = 4,
            SnapshotId = "11111111111111111111111111111111", Outposts = new OutpostRecoveryData {
                Metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = OutpostReconciliationTests.Scope() } } },
        RawSha256: Enumerable.Repeat(hash, 32).ToArray());
    private static LoginReply Login() => new() { SaveGeneration = 3, SaveWriteEpoch = 4, OutpostVersion = 1,
        OutpostRestoreBoundary = "boundary-1", Player = new PlayerRef { PlayerId = 7 } };

    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    [InlineData("cancel")]
    [InlineData("mutate")]
    [InlineData("delivery")]
    public void Every_mutating_kind_is_durable_prepared_and_keeps_exact_payload(string kind)
    {
        var request = Request(kind); var bytes = request.ToByteArray(); var entry = Open().Prepare(request);
        Assert.Equal(bytes, Assert.Single(Open().Entries).Payload); Assert.Equal(OutboxPhase.Prepared, entry.Phase);
        Assert.Equal(request, TransferOutbox.Decode(entry)); Assert.False(TransferOutbox.ReadyFor(entry, Login()));
    }

    [Fact]
    public void Prepared_entry_is_not_replayed_as_removed_by_legacy_tick_or_receipt()
    {
        var outbox = Open(); var entry = outbox.Prepare(Request("commit"));
        outbox.ReconcileSnapshot(new SaveRecoveryData { Version = 1, Generation = 3, WriteEpoch = 4,
            SnapshotId = "11111111111111111111111111111111", Removed = [entry.Id] }, accepted: true, gameTick: 1000);
        Assert.Equal(OutboxPhase.Prepared, Assert.Single(Open().Entries).Phase);
        Assert.False(TransferOutbox.ReadyFor(Assert.Single(Open().Entries), Login()));
    }

    [Fact]
    public void Ready_requires_exact_accepted_proof_without_rewriting_request()
    {
        var outbox = Open(); var entry = outbox.Prepare(Request("commit")); var original = entry.Payload.ToArray();
        outbox.ReconcileSnapshot(Meta(), accepted: false); Assert.Equal(OutboxPhase.Prepared, Open().Entries[0].Phase);
        outbox.ReconcileSnapshot(Meta(8), accepted: true); Assert.Equal(OutboxPhase.Prepared, Open().Entries[0].Phase);
        outbox.ReconcileSnapshot(Meta(), accepted: true); var ready = Assert.Single(Open().Entries);
        Assert.Equal(OutboxPhase.Ready, ready.Phase); Assert.True(TransferOutbox.ReadyFor(ready, Login()));
        Assert.Equal(original, ready.Payload); var old = Login(); old.OutpostRestoreBoundary = "another";
        Assert.False(TransferOutbox.ReadyFor(ready, old));
    }

    [Fact]
    public void Old_boundary_is_quarantined_without_rewriting_or_discarding_other_work()
    {
        var outbox = Open(); var old = outbox.Prepare(Request("commit"));
        var scope = OutpostReconciliationTests.Scope(); scope.RestoreBoundary = "new-boundary";
        var currentRequest = (OutpostProofRequest)Request("commit");
        currentRequest.Scope = scope.Clone();
        currentRequest.PostRemoval.Scope = scope.Clone();
        currentRequest.ClientRequestId = Guid.NewGuid().ToString("N");
        var current = outbox.Prepare(currentRequest);
        var unscoped = outbox.Add(new DepositRequest { ClientRequestId = "deposit", Silver = 1 });
        byte[] original = File.ReadAllBytes(PathName);
        string? kept = outbox.QuarantineOutsideScope(scope); Assert.NotNull(kept);
        Assert.Equal(original, File.ReadAllBytes(kept));
        var reopened = Open();
        Assert.DoesNotContain(reopened.Entries, e => e.Id == old.Id);
        Assert.Equal(new[] { current.Id, unscoped.Id }, reopened.Entries.Select(e => e.Id));
        Assert.True(reopened.HasUnresolved);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    [InlineData("cancel")]
    [InlineData("mutate")]
    [InlineData("delivery")]
    public void Refusal_never_turns_outpost_obligation_into_guessed_local_return(string kind)
    {
        var outbox = Open(); var entry = outbox.Prepare(Request(kind)); outbox.Refuse(entry.Id, "server refused");
        Assert.Equal(OutboxPhase.RecoveryRequired, Assert.Single(Open().Entries).Phase);
        Assert.Equal(entry.Payload, Open().Entries[0].Payload);
    }

    [Fact]
    public void Begin_authorization_is_durable_but_not_a_materialization_receipt()
    {
        var outbox = Open(); var request = (OutpostDeliveryRequest)Request("delivery"); var entry = outbox.Prepare(request);
        outbox.ReconcileSnapshot(Meta(), accepted: true);
        outbox.RecordOutpostReply(entry.Id, new OutpostDeliveryReply { AttemptId = Guid.NewGuid().ToString("D"), Authorization = request.Clone() });
        Assert.Single(Open().Entries); Assert.Equal(OutboxPhase.Ready, Open().Entries[0].Phase); Assert.True(Open().HasUnresolved);
        Assert.Equal(request, TransferOutbox.Decode(Open().Entries[0])); Assert.Equal(entry.Payload, Open().Entries[0].Payload);
    }

    [Fact]
    public async Task Offline_snapshot_bytes_are_never_rewritten_when_boundary_changes()
    {
        Directory.CreateDirectory(directory); var recovery = Meta().Recovery!;
        byte[] packed = Rimlink.Server.Tests.Support.RecoveryFixtures.Save(1000, recovery.SnapshotId, generation: 3, epoch: 4,
            outposts: recovery.Outposts);
        byte[] raw = Rimlink.Server.Tests.Support.SaveFixtures.Decompress(packed); string primary = Path.Combine(directory, "primary.rws");
        await File.WriteAllBytesAsync(primary, raw); var pending = new PendingUpload(directory);
        pending.BeginSnapshot(recovery.SnapshotId, 3, 4); var captured = pending.CaptureSnapshot(primary);
        Assert.Equal(SHA256.HashData(raw), captured.Meta.RawSha256);
        var queue = new SaveUploadQueue(); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = queue.Enqueue(captured, async (snapshot, ct) => {
            reached.SetResult(); await release.Task; Assert.Equal(raw, await File.ReadAllBytesAsync(snapshot.Path, ct)); return new SaveInfo(); }, default);
        await reached.Task; try {
            var outbox = Open(); outbox.Prepare(Request("commit")); var scope = OutpostReconciliationTests.Scope(); scope.RestoreBoundary = "new";
            outbox.QuarantineOutsideScope(scope); await File.WriteAllTextAsync(primary, "new unrelated save");
        } finally { release.TrySetResult(); }
        await queued; Assert.Equal(raw, await File.ReadAllBytesAsync(captured.Path));
        Assert.Equal("boundary-1", captured.Meta.Recovery!.Outposts.Metadata.Scope.RestoreBoundary);
    }

    [Fact]
    public async Task Queued_snapshot_metadata_does_not_alias_mutable_game_evidence()
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "fixed.rws"); await File.WriteAllTextAsync(path, "immutable bytes");
        var firstMeta = Meta(); var first = new SaveSnapshot(firstMeta.Recovery!.SnapshotId, path, firstMeta);
        var secondMeta = Meta(); var second = new SaveSnapshot(secondMeta.Recovery!.SnapshotId, path, secondMeta);
        var queue = new SaveUploadQueue(); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = queue.Enqueue(first, async (_, _) => { reached.SetResult(); await release.Task; return new SaveInfo(); }, default);
        await reached.Task; var b = queue.Enqueue(second, (saved, _) => {
            Assert.Equal("boundary-1", saved.Meta.Recovery!.Outposts.Metadata.Scope.RestoreBoundary);
            Assert.Equal((byte)9, saved.Meta.RawSha256![0]); return Task.FromResult(new SaveInfo()); }, default);
        secondMeta.Recovery!.Outposts.Metadata.Scope.RestoreBoundary = "mutated-after-enqueue"; secondMeta.RawSha256![0] = 0;
        release.TrySetResult(); await Task.WhenAll(a, b);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
