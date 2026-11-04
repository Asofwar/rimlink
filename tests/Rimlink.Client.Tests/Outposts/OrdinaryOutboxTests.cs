using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Rimlink.Client.Economy;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Client.Tests.Outposts;

public sealed partial class OrdinaryOutboxTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rimlink-ordinary-outbox", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(directory, "outbox.json");
    private TransferOutbox Open() => new(PathName, new ConsoleLog(), 3, 4);
    private static OutpostScopeMessage Scope() => new() { WorldInstanceId = "world", OwnerPlayerId = 7,
        SaveGeneration = 3, SaveWriteEpoch = 4, RestoreBoundary = "boundary" };
    private static TransferRequest Request() => new() { ClientRequestId = "22222222222222222222222222222222", ToPlayerId = 8,
        GameTick = 1234, Silver = 15, Pawns = new() { Pawns = { new PawnBlob { ScribeXmlZstd = ByteString.CopyFromUtf8("current-disabled-pawn"),
            Registry = new() { SchemaVersion = 1, Scope = Scope(), PawnCustodyId = "33333333-3333-3333-3333-333333333333",
                AssignmentId = "", PawnReferenceToken = "Thing_Human57" } } } } };
    private static LoginReply Login() => new() { SaveGeneration = 3, SaveWriteEpoch = 4, OutpostVersion = 1,
        OutpostRestoreBoundary = "boundary", Player = new() { PlayerId = 7 } };
    private static SaveMeta Meta(OutboxEntry entry, string fault = "")
    {
        var capture = OrdinaryOutgoingCapture.Parser.ParseFrom(entry.Ordinary!.Capture);
        var tombstone = Request().Pawns.Pawns[0].Registry.Clone(); tombstone.Tombstone = true;
        var metadata = new OutpostSaveMetadata { SchemaVersion = 1, Scope = Scope(), Registry = { tombstone }, OrdinaryOutgoing = { capture } };
        string[] removed = [entry.Id]; string[] returned = [];
        switch (fault)
        {
            case "capture": metadata.OrdinaryOutgoing.Clear(); break;
            case "removed": removed = [Request().ClientRequestId]; break;
            case "alive": tombstone.Tombstone = false; break;
            case "registry": tombstone.PawnReferenceToken = "Thing_Human58"; break;
            case "scope": metadata.Scope.RestoreBoundary = "other-boundary"; capture.Scope.RestoreBoundary = "other-boundary";
                capture.Pawns[0].Pawn.Registry.Scope.RestoreBoundary = "other-boundary";
                capture.Pawns[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(capture.Pawns[0].Pawn.ToByteArray()));
                tombstone.Scope.RestoreBoundary = "other-boundary"; break;
            case "returned": returned = [entry.Id]; break;
        }
        return new("seed", "Nova", 9876, SaveGeneration: 3, Recovery: new SaveRecoveryData { Version = 1, Generation = 3, WriteEpoch = 4,
            SnapshotId = "11111111111111111111111111111111", Removed = removed, Returned = returned,
            Outposts = new() { Metadata = metadata } }, RawSha256: Enumerable.Repeat((byte)(fault == "hash" ? 8 : 9), 32).ToArray());
    }

    [Fact]
    public void Accepted_removal_freezes_final_request_and_binds_local_id_not_client_id()
    {
        var request = Request(); byte[] original = request.ToByteArray(); var box = Open();
        var entry = box.PrepareOrdinary(request, Scope()); var prepared = Assert.Single(Open().Entries);
        var capture = OrdinaryOutgoingCapture.Parser.ParseFrom(prepared.Ordinary!.Capture);
        Assert.Equal(entry.Id, capture.LocalOperationId); Assert.NotEqual(request.ClientRequestId, capture.LocalOperationId);
        Assert.Equal(SHA256.HashData(original), capture.RequestIntentSha256.ToByteArray());
        Assert.Equal(OutboxPhase.Prepared, prepared.Phase); Assert.False(TransferOutbox.ReadyFor(prepared, Login()));
        var meta = Meta(prepared); box.BindOrdinarySnapshot(entry.Id, meta); box.ReconcileSnapshot(meta, accepted: true);
        var ready = Assert.Single(Open().Entries);
        Assert.Equal(OutboxPhase.Ready, ready.Phase); Assert.True(TransferOutbox.ReadyFor(ready, Login()));
        Assert.Equal(original, ready.Payload); Assert.Equal(1234, ((TransferRequest)TransferOutbox.Decode(ready)).GameTick);
        Assert.Equal(meta.RawSha256, OutpostSnapshotProof.Parser.ParseFrom(ready.Ordinary!.AcceptedProof).RawSha256.ToByteArray());
    }

    [Theory]
    [InlineData("capture")][InlineData("removed")][InlineData("alive")][InlineData("registry")]
    [InlineData("scope")][InlineData("returned")][InlineData("hash")]
    public void Selected_or_accepted_nonmatching_snapshot_cannot_unlock_frozen_request(string fault)
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); box.BindOrdinarySnapshot(entry.Id, Meta(entry));
        box.ReconcileSnapshot(Meta(entry, fault), accepted: true, selected: true);
        var kept = Assert.Single(Open().Entries); Assert.False(TransferOutbox.ReadyFor(kept, Login()));
        Assert.Equal(Request().ToByteArray(), kept.Payload);
    }

    [Fact]
    public void Legacy_removed_receipt_alone_cannot_unlock_or_rewrite_protected_request()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); box.BindOrdinarySnapshot(entry.Id, Meta(entry));
        box.ReconcileSnapshot(Meta(entry).Recovery!, accepted: true, gameTick: 9876);
        var kept = Assert.Single(Open().Entries); Assert.Equal(OutboxPhase.Prepared, kept.Phase);
        Assert.Equal(Request().ToByteArray(), kept.Payload); Assert.False(TransferOutbox.ReadyFor(kept, Login()));
    }

    [Fact]
    public void Ordinary_refusal_is_not_a_local_return_authorization()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); box.Refuse(entry.Id, "rejected");
        var kept = Assert.Single(Open().Entries); Assert.NotEqual(OutboxPhase.Returning, kept.Phase);
        Assert.False(TransferOutbox.ReadyFor(kept, Login())); Assert.Equal(entry.Payload, kept.Payload);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void New_tagged_request_cannot_use_an_unscoped_legacy_journal(bool add)
    {
        var box = Open(); Assert.Throws<InvalidDataException>(() => add ? box.Add(Request()) : box.Prepare(Request()));
        Assert.Empty(Open().Entries);
    }

    [Fact]
    public void Old_tagged_journal_without_capture_stays_preserved_and_never_becomes_ready_or_returned()
    {
        Directory.CreateDirectory(directory);
        var entry = new OutboxEntry { Id = "44444444444444444444444444444444", Kind = OutboxKind.Send, Payload = Request().ToByteArray(),
            Phase = OutboxPhase.Ready, SaveGeneration = 3, SaveWriteEpoch = 4 };
        File.WriteAllBytes(PathName, JsonSerializer.SerializeToUtf8Bytes(new[] { entry }));
        Assert.False(TransferOutbox.ReadyFor(Assert.Single(Open().Entries), Login()));
        var box = Open(); box.ReconcileSnapshot(new SaveRecoveryData { Version = 1, Generation = 3, WriteEpoch = 4,
            SnapshotId = "11111111111111111111111111111111", Removed = [entry.Id], Returned = [entry.Id] }, accepted: true, selected: true);
        var kept = Assert.Single(Open().Entries); Assert.Equal(OutboxPhase.RecoveryRequired, kept.Phase); Assert.Equal(entry.Payload, kept.Payload);
    }

    [Theory]
    [InlineData("world")][InlineData("owner")][InlineData("generation")][InlineData("epoch")][InlineData("boundary")]
    public void Scope_change_quarantines_the_original_ordinary_document_byte_exactly(string changed)
    {
        var box = Open(); box.PrepareOrdinary(Request(), Scope()); byte[] original = File.ReadAllBytes(PathName);
        var scope = Scope();
        switch (changed)
        {
            case "world": scope.WorldInstanceId = "other-world"; break;
            case "owner": scope.OwnerPlayerId++; break;
            case "generation": scope.SaveGeneration++; break;
            case "epoch": scope.SaveWriteEpoch++; break;
            case "boundary": scope.RestoreBoundary = "other-boundary"; break;
        }
        string? kept = box.QuarantineOutsideScope(scope); Assert.NotNull(kept);
        Assert.Equal(original, File.ReadAllBytes(kept)); Assert.Empty(Open().Entries);
    }

    [Fact]
    public void A_bound_removal_proof_is_not_rebased_to_another_accepted_file()
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var first = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, first); byte[] before = File.ReadAllBytes(PathName);
        Assert.Throws<InvalidDataException>(() => box.BindOrdinarySnapshot(entry.Id, Meta(entry, "hash")));
        Assert.Equal(before, File.ReadAllBytes(PathName));
        box.ReconcileSnapshot(first, accepted: true); Assert.True(TransferOutbox.ReadyFor(Assert.Single(Open().Entries), Login()));
    }

    [Theory]
    [InlineData("payload")][InlineData("capture")][InlineData("accepted-proof")][InlineData("owner")][InlineData("boundary")][InlineData("version")]
    public void A_ready_journal_does_not_authorize_changed_intent_proof_or_login(string fault)
    {
        var box = Open(); var entry = box.PrepareOrdinary(Request(), Scope()); var meta = Meta(entry);
        box.BindOrdinarySnapshot(entry.Id, meta); box.ReconcileSnapshot(meta, accepted: true);
        var ready = Assert.Single(Open().Entries); var login = Login();
        switch (fault)
        {
            case "payload": var request = (TransferRequest)TransferOutbox.Decode(ready); request.Silver++; ready.Payload = request.ToByteArray(); break;
            case "capture": var capture = OrdinaryOutgoingCapture.Parser.ParseFrom(ready.Ordinary!.Capture);
                capture.Pawns[0].Pawn.Registry.PawnReferenceToken = "Thing_Human58";
                capture.Pawns[0].BlobSha256 = ByteString.CopyFrom(SHA256.HashData(capture.Pawns[0].Pawn.ToByteArray()));
                ready.Ordinary.Capture = capture.ToByteArray(); break;
            case "accepted-proof": var proof = OutpostSnapshotProof.Parser.ParseFrom(ready.Ordinary!.AcceptedProof);
                proof.RawSha256 = ByteString.CopyFrom(new byte[32]); ready.Ordinary.AcceptedProof = proof.ToByteArray(); break;
            case "owner": login.Player.PlayerId++; break;
            case "boundary": login.OutpostRestoreBoundary = "other-boundary"; break;
            case "version": login.OutpostVersion = 0; break;
        }
        Assert.False(TransferOutbox.ReadyFor(ready, login));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
