using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.GuildCapture;

public sealed class GuildDeliveryJournalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-guild-journal", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "outbox.json");
    private TransferOutbox Open(GuildDeliveryRequest r) => new(PathName, new ConsoleLog(), r.Scope.SaveGeneration, r.Scope.SaveWriteEpoch);
    private static SaveMeta Snapshot(GuildDeliveryRequest r, OutpostSaveMetadata metadata, bool before = true) => new("seed", "guild", 100,
        SaveGeneration: r.Scope.SaveGeneration, RawSha256: before ? r.BeforeSnapshot.RawSha256.ToByteArray() : Enumerable.Repeat((byte)9, 32).ToArray(),
        Recovery: new SaveRecoveryData { Version = 1, Generation = r.Scope.SaveGeneration, WriteEpoch = r.Scope.SaveWriteEpoch,
            SnapshotId = before ? r.BeforeSnapshot.SnapshotId : Guid.NewGuid().ToString("N"), Outposts = new() { Metadata = metadata.Clone() } });
    private static LoginReply Login(GuildDeliveryRequest r) => new() { GuildVersion = 1, SaveGeneration = r.Scope.SaveGeneration,
        SaveWriteEpoch = r.Scope.SaveWriteEpoch, OutpostRestoreBoundary = r.Scope.RestoreBoundary, Player = new() { PlayerId = r.Scope.PlayerId } };

    [Fact]
    public void Exact_before_upload_and_reply_are_durable_before_spawn()
    {
        var (parcel, request, reply, metadata) = GuildDeliveryEvidenceTests.Exchange(); var box = Open(request);
        string branch = Guid.NewGuid().ToString("D"); var entry = box.PrepareGuildDelivery(parcel, request, branch);
        Assert.True(box.HasUnresolved); Assert.Equal(request, TransferOutbox.Decode(entry));
        Assert.False(TransferOutbox.ReadyFor(entry, Login(request)));
        Assert.Throws<InvalidDataException>(() => box.MarkGuildSpawnStarted(entry.Id, branch));
        var wrong = Snapshot(request, metadata) with { RawSha256 = new byte[32].Select(_ => (byte)7).ToArray() };
        box.ReconcileSnapshot(wrong, true); Assert.Equal(OutboxPhase.Prepared, entry.Phase);
        box.ReconcileSnapshot(Snapshot(request, metadata), false); Assert.Equal(OutboxPhase.Prepared, entry.Phase);
        box.ReconcileSnapshot(Snapshot(request, metadata), true); Assert.True(TransferOutbox.ReadyFor(entry, Login(request)));
        box.RecordGuildReply(entry.Id, reply); box.MarkGuildSpawnStarted(entry.Id, branch);
        var reopened = Open(request); var saved = Assert.Single(reopened.Entries);
        Assert.Equal(parcel.ToByteArray(), saved.Guild!.Parcel); Assert.True(saved.Guild.SpawnStarted);
        Assert.Equal(reply.ToByteArray(), saved.Guild.Authorization); Assert.True(reopened.HasUnresolved);
        Assert.Throws<InvalidDataException>(() => reopened.MarkGuildSpawnStarted(saved.Id, branch));
        Assert.Throws<InvalidDataException>(() => reopened.Remove(saved.Id));
    }

    [Fact]
    public void Legacy_parcel_ids_and_a_reply_never_prove_physical_acceptance()
    {
        var (parcel, request, reply, metadata) = GuildDeliveryEvidenceTests.Exchange(); var box = Open(request);
        var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
        var snapshot = Snapshot(request, metadata); snapshot = snapshot with { Recovery = snapshot.Recovery! with { ParcelIds = [parcel.Id] } };
        box.ReconcileSnapshot(snapshot.Recovery, true); Assert.Equal(OutboxPhase.Prepared, entry.Phase);
        box.ReconcileSnapshot(snapshot, true); box.RecordGuildReply(entry.Id, reply);
        Assert.Null(entry.Guild!.AcceptedReceipt); Assert.True(box.HasUnresolved);
        Assert.True(box.Refuse(entry.Id, "not issued")); Assert.Equal(OutboxPhase.RecoveryRequired, Open(request).Entries[0].Phase);
    }

    [Fact]
    public void Only_real_accepted_receipt_can_close_and_cold_selection_cannot_respawn()
    {
        var (parcel, request, reply, metadata) = GuildDeliveryEvidenceTests.Exchange(); var box = Open(request);
        var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
        box.ReconcileSnapshot(Snapshot(request, metadata), true); box.RecordGuildReply(entry.Id, reply);
        box.MarkGuildSpawnStarted(entry.Id, entry.Guild!.BranchId);
        metadata.GuildMetadata.Receipts.Add(Rimlink.Client.GuildCapture.GuildDeliveryEvidence.Receipt(parcel, request, reply));
        var post = Snapshot(request, metadata, false);
        box.ReconcileSnapshot(post, false); Assert.Null(entry.Guild.AcceptedReceipt);
        box.ReconcileSnapshot(post, true); Assert.NotNull(Open(request).Entries[0].Guild!.AcceptedReceipt);
        Assert.Throws<InvalidDataException>(() => box.Remove(entry.Id));
        reply.AckOnly = true; reply.AcceptedSnapshot = new() { Scope = request.Scope.Clone(), SnapshotId = post.Recovery!.SnapshotId,
            RawSha256 = ByteString.CopyFrom(post.RawSha256!) };
        box.RecordGuildReply(entry.Id, reply); box.Remove(entry.Id); Assert.Empty(Open(request).Entries);
    }

    [Theory]
    [InlineData("player")][InlineData("boundary")][InlineData("version")][InlineData("epoch")]
    public void Replay_cannot_cross_authentication_scope(string changed)
    {
        var (parcel, request, _, metadata) = GuildDeliveryEvidenceTests.Exchange(); var box = Open(request);
        var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
        box.ReconcileSnapshot(Snapshot(request, metadata), true); var login = Login(request);
        Assert.True(TransferOutbox.ReadyFor(entry, login));
        if (changed == "player") login.Player.PlayerId++;
        if (changed == "boundary") login.OutpostRestoreBoundary += "changed";
        if (changed == "version") login.GuildVersion = 0;
        if (changed == "epoch") login.SaveWriteEpoch++;
        Assert.False(TransferOutbox.ReadyFor(entry, login));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Invalid_or_missing_receipt_on_selected_spawned_branch_is_held(bool conflicting)
    {
        var (parcel, request, reply, metadata) = GuildDeliveryEvidenceTests.Exchange(); var box = Open(request);
        var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
        box.ReconcileSnapshot(Snapshot(request, metadata), true); box.RecordGuildReply(entry.Id, reply);
        box.MarkGuildSpawnStarted(entry.Id, entry.Guild!.BranchId);
        if (conflicting) { var r = Rimlink.Client.GuildCapture.GuildDeliveryEvidence.Receipt(parcel, request, reply); r.PayloadDigest = ByteString.CopyFrom(new byte[32]); metadata.GuildMetadata.Receipts.Add(r); }
        box.ReconcileSnapshot(Snapshot(request, metadata), true, selected: true);
        Assert.Equal(OutboxPhase.RecoveryRequired, Open(request).Entries[0].Phase);
    }

    [Fact]
    public void Restore_boundary_keeps_unresolved_old_guild_journal_bytes_outside_the_replay_queue()
    {
        var (parcel, request, reply, metadata) = GuildDeliveryEvidenceTests.Exchange();
        var box = Open(request);
        var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
        box.ReconcileSnapshot(Snapshot(request, metadata), true);
        box.RecordGuildReply(entry.Id, reply);
        box.MarkGuildSpawnStarted(entry.Id, entry.Guild!.BranchId);
        byte[] original = File.ReadAllBytes(PathName);
        Assert.Null(box.QuarantineOutsideScope(metadata.Scope));
        var current = metadata.Scope.Clone();
        current.RestoreBoundary = Guid.NewGuid().ToString("D");
        string? retained = box.QuarantineOutsideScope(current);
        Assert.NotNull(retained);
        Assert.Equal(original, File.ReadAllBytes(retained));
        Assert.False(Open(request).HasUnresolved);
    }

    [Fact]
    public void Tampered_disk_payload_is_preserved_and_never_loaded_as_replayable()
    {
        var (parcel, request, _, _) = GuildDeliveryEvidenceTests.Exchange(); var box = Open(request);
        var entry = box.PrepareGuildDelivery(parcel, request, Guid.NewGuid().ToString("D"));
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PathName))!;
        request.PayloadDigest = ByteString.CopyFrom(new byte[32]); document[0]!["Payload"] = Convert.ToBase64String(request.ToByteArray());
        File.WriteAllText(PathName, document.ToJsonString()); byte[] original = File.ReadAllBytes(PathName);
        Assert.Throws<IOException>(() => Open(request)); Assert.Equal(original, File.ReadAllBytes(PathName));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
