using System.Security.Cryptography;
using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

/// <summary>Only bounded protobuf bytes are persisted: generated repeated collections are never JSON-rehydrated lossily.</summary>
public sealed class OutpostJournal
{
    public byte[] Scope { get; set; } = [];
    public byte[] RequiredProof { get; set; } = [];
    public byte[] IntentDigest { get; set; } = [];
    public byte[]? AcceptedProof { get; set; }
    public byte[]? AcceptedReceipt { get; set; }
    public byte[]? DeliveryReply { get; set; }
    public bool ReplyUnknown { get; set; }
    public string? DeliveryBranchId { get; set; }
    public bool SpawnStarted { get; set; }
}

public sealed partial class TransferOutbox
{
    private static bool IsOutpost(OutboxKind kind) => kind is OutboxKind.OutpostPrepare or OutboxKind.OutpostCommit
        or OutboxKind.OutpostCancel or OutboxKind.OutpostMutate or OutboxKind.OutpostBeginDelivery;
    private static (OutpostScopeMessage Scope, string Id, ByteString Digest, OutpostSnapshotProof Proof) OutpostRequest(IMessage request) => request switch
    {
        OutpostPrepareRequest r => (r.Scope, r.ClientRequestId, r.IntentDigest, r.PreRemoval),
        OutpostProofRequest r => (r.Scope, r.ClientRequestId, r.IntentDigest, r.PostRemoval),
        OutpostCancelRequest r => (r.Scope, r.ClientRequestId, r.IntentDigest, r.CancellationProof),
        OutpostMutationRequest r => (r.Scope, r.ClientRequestId, r.IntentDigest, r.CanonicalSnapshot),
        OutpostDeliveryRequest r => (r.Scope, r.ClientRequestId, ByteString.CopyFrom(SHA256.HashData(r.ToByteArray())), r.CanonicalSnapshot),
        _ => throw new InvalidDataException("not an outpost request"),
    };
    private OutpostJournal NewOutpostJournal(IMessage request)
    {
        var r = OutpostRequest(request);
        if (request.CalculateSize() > Limits.MaxMessageBytes || !OutpostReconciliation.ValidScope(r.Scope)
            || r.Scope.SaveGeneration != generation || r.Scope.SaveWriteEpoch != epoch || r.Id.Length is 0 or > 128
            || r.Digest.Length != 32 || !OutpostReconciliation.ValidProof(r.Proof)
            || !OutpostReconciliation.SameScope(r.Scope, r.Proof.Scope)) throw new InvalidDataException("invalid outpost journal intent");
        return new() { Scope = r.Scope.ToByteArray(), RequiredProof = r.Proof.ToByteArray(), IntentDigest = r.Digest.ToByteArray() };
    }
    private static T Parse<T>(byte[] bytes, MessageParser<T> parser) where T : IMessage<T>
    {
        if (bytes.Length > Limits.MaxMessageBytes) throw new InvalidDataException("oversized outpost journal evidence");
        using var stream = new MemoryStream(bytes, writable: false);
        using var input = CodedInputStream.CreateWithLimits(stream, Limits.MaxMessageBytes, 32);
        return parser.ParseFrom(input);
    }
    private static OutpostLocalEvidence Evidence(OutboxEntry entry)
    {
        var journal = entry.Outpost ?? throw new InvalidDataException("missing outpost journal safety scope");
        var r = OutpostRequest(Decode(entry));
        var scope = Parse(journal.Scope, OutpostScopeMessage.Parser); var proof = Parse(journal.RequiredProof, OutpostSnapshotProof.Parser);
        if (!OutpostReconciliation.SameScope(scope, r.Scope) || !proof.Equals(r.Proof)
            || !journal.IntentDigest.AsSpan().SequenceEqual(r.Digest.Span)) throw new InvalidDataException("outpost journal intent changed");
        return new(scope, entry.Phase, r.Id, r.Digest, proof,
            journal.AcceptedProof is null ? null : Parse(journal.AcceptedProof, OutpostSnapshotProof.Parser),
            journal.AcceptedReceipt is null ? null : Parse(journal.AcceptedReceipt, OutpostTransitionReceipt.Parser), journal.ReplyUnknown);
    }
    private static bool OutpostReadyFor(OutboxEntry entry, LoginReply login)
    {
        try
        {
            var e = Evidence(entry);
            return login.OutpostVersion == 1 && e.Scope.OwnerPlayerId == login.Player.PlayerId
                && e.Scope.RestoreBoundary == login.OutpostRestoreBoundary && entry.Phase == OutboxPhase.Ready
                && OutpostReconciliation.Decide(e with { ReplyUnknown = false }, new() { Scope = e.Scope })
                    is OutpostRecoveryDecision.Replay or OutpostRecoveryDecision.AckOnly;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException) { return false; }
    }
    public void ReconcileSnapshot(SaveMeta snapshot, bool accepted, bool selected = false)
    {
        if (snapshot.Recovery is not { } recovery) return;
        ReconcileSnapshot(recovery, accepted, snapshot.GameTick, selected);
        ReconcileOrdinarySnapshot(snapshot, accepted, selected);
        ReconcileGuildSnapshot(snapshot, accepted, selected);
        if (!accepted || !recovery.Outposts.Present || snapshot.RawSha256?.Length != 32) return;
        var scope = recovery.Outposts.Metadata.Scope;
        var proof = new OutpostSnapshotProof { Scope = scope.Clone(), SnapshotId = recovery.SnapshotId, RawSha256 = ByteString.CopyFrom(snapshot.RawSha256) };
        lock (gate)
        {
            foreach (var entry in entries.Where(e => IsOutpost(e.Kind) && e.Phase != OutboxPhase.RecoveryRequired))
            {
                var local = Evidence(entry);
                if (!OutpostReconciliation.SameScope(local.Scope, scope)) { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                var receipt = recovery.Outposts.Metadata.Receipts.FirstOrDefault(p => p.ClientRequestId == local.ClientRequestId
                    && p.IntentDigest.Equals(local.IntentDigest) && p.Snapshot is not null && p.Snapshot.Equals(local.RequiredProof));
                var decision = OutpostReconciliation.Decide(local with { Phase = OutboxPhase.Ready, AcceptedProof = proof, AcceptedReceipt = receipt },
                    new() { Scope = scope });
                if (decision is not (OutpostRecoveryDecision.Replay or OutpostRecoveryDecision.QueryOperation or OutpostRecoveryDecision.AckOnly)) continue;
                entry.Outpost!.AcceptedProof = proof.ToByteArray(); entry.Outpost.AcceptedReceipt = receipt?.ToByteArray();
                entry.SnapshotId = proof.SnapshotId; entry.Phase = OutboxPhase.Ready;
            }
            Save();
        }
    }
    public string? QuarantineOutsideScope(OutpostScopeMessage scope)
    {
        if (!OutpostReconciliation.ValidScope(scope)) throw new InvalidDataException("invalid current outpost boundary");
        lock (gate)
        {
            var old = entries.Where(e => IsOutpost(e.Kind) && !OutpostReconciliation.SameScope(Evidence(e).Scope, scope)
                || e.Ordinary is not null && !OutpostReconciliation.SameScope(ReadOrdinary(e).Scope, scope)
                || e.Kind == OutboxKind.GuildBeginDelivery && OutsideScope(ReadGuild(e).Request.Scope)).ToArray();
            if (old.Length == 0) return null;
            string kept = path + $".before-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json";
            WriteDocument(kept, File.ReadAllBytes(path)); // Preserve the original document byte for byte before removing old work.
            foreach (var entry in old) entries.Remove(entry);
            Save(); return kept;
        }
        bool OutsideScope(GuildColonyScope original) => original.WorldInstanceId != scope.WorldInstanceId || original.PlayerId != scope.OwnerPlayerId
            || original.SaveGeneration != scope.SaveGeneration || original.SaveWriteEpoch != scope.SaveWriteEpoch
            || original.RestoreBoundary != scope.RestoreBoundary;
    }
    private bool QuarantineForLogin(OutboxEntry entry, LoginReply? login, OutpostClient? client)
    {
        if (login is null || login.OutpostVersion != 1) return false;
        try
        {
            var local = Evidence(entry); var current = client?.Scope ?? local.Scope.Clone();
            current.OwnerPlayerId = login.Player.PlayerId; current.SaveGeneration = login.SaveGeneration;
            current.SaveWriteEpoch = login.SaveWriteEpoch; current.RestoreBoundary = login.OutpostRestoreBoundary;
            if (OutpostReconciliation.SameScope(local.Scope, current)) return false;
            QuarantineOutsideScope(current); return true;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
        { HoldForRecovery(entry.Id, new Refusal(null, "outpost journal evidence is invalid", null, [])); return true; }
    }
    public void ArmOutpostDelivery(string id, string gameBranchId)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id); var journal = entry.Outpost;
            if (entry.Kind != OutboxKind.OutpostBeginDelivery || entry.Phase == OutboxPhase.RecoveryRequired || journal is null
                || !Guid.TryParseExact(gameBranchId, "D", out var branch) || branch == Guid.Empty
                || journal.DeliveryBranchId is not null && journal.DeliveryBranchId != gameBranchId || journal.SpawnStarted)
                throw new InvalidDataException("delivery branch cannot prove absence of a prior spawn");
            var prior = journal.DeliveryBranchId; journal.DeliveryBranchId = gameBranchId;
            try { Save(); } catch { journal.DeliveryBranchId = prior; throw; }
        }
    }
    public void MarkOutpostSpawnStarted(string id, string gameBranchId)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id); var journal = entry.Outpost;
            if (entry.Kind != OutboxKind.OutpostBeginDelivery || entry.Phase != OutboxPhase.Ready || journal is null
                || journal.DeliveryBranchId != gameBranchId || journal.DeliveryReply is null || journal.SpawnStarted)
                throw new InvalidDataException("delivery has no continuous durable pre-spawn authorization");
            journal.SpawnStarted = true;
            try { Save(); } catch { journal.SpawnStarted = false; throw; }
        }
    }
    public void MarkReplyUnknown(string id)
    {
        lock (gate)
        {
            if (entries.SingleOrDefault(e => e.Id == id)?.Outpost is not { } journal) return;
            bool prior = journal.ReplyUnknown; journal.ReplyUnknown = true;
            try { Save(); } catch { journal.ReplyUnknown = prior; throw; }
        }
    }
    public void RecordOutpostReply(string id, OutpostDeliveryReply reply)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id);
            if (entry.Kind != OutboxKind.OutpostBeginDelivery || reply.Authorization is null || reply.CalculateSize() > Limits.MaxMessageBytes
                || !reply.Authorization.Equals(Decode(entry)) || !Guid.TryParse(reply.AttemptId, out _))
                throw new InvalidDataException("delivery authorization does not match the durable intent");
            var journal = entry.Outpost ?? throw new InvalidDataException("missing delivery journal");
            var prior = (journal.DeliveryReply, journal.ReplyUnknown); journal.DeliveryReply = reply.ToByteArray(); journal.ReplyUnknown = false;
            try { Save(); } catch { (journal.DeliveryReply, journal.ReplyUnknown) = prior; throw; }
        }
    }
    private async Task<bool?> AttemptOutpost(OutpostClient client, OutboxEntry entry, CancellationToken ct)
    {
        var e = Evidence(entry);
        if (e.ReplyUnknown && entry.Kind != OutboxKind.OutpostBeginDelivery)
        {
            var known = await client.Operation(new() { Scope = e.Scope.Clone(), ClientRequestId = e.ClientRequestId, IntentDigest = e.IntentDigest }, ct);
            if (Completed(known)) return true;
            if (entry.Kind == OutboxKind.OutpostPrepare && known.Status == OutpostOperationStatus.Prepared) return null;
            if (known.Status != OutpostOperationStatus.Prepared) return null;
        }
        switch (Decode(entry))
        {
            case OutpostPrepareRequest r:
                var prepared = await client.Prepare(r, ct);
                if (Completed(prepared)) return true;
                // Prepared authorizes a later game action; it is not a completed operation or a reason to discard its only journal.
                MarkReplyUnknown(entry.Id); return null;
            case OutpostProofRequest r: return Finish(await client.Commit(r, ct));
            case OutpostCancelRequest r: return Finish(await client.Cancel(r, ct));
            case OutpostMutationRequest r: return Finish(await client.Mutate(r, ct));
            case OutpostDeliveryRequest r:
                var reply = await client.BeginDelivery(r, ct); RecordOutpostReply(entry.Id, reply);
                return reply.AckOnly && e.AcceptedReceipt is not null ? true : null;
            default: throw new InvalidDataException("unsupported outpost replay");
        }
        bool Completed(OutpostOperationReply reply) => reply.Status == OutpostOperationStatus.Committed
            || entry.Kind != OutboxKind.OutpostMutate && reply.Status == OutpostOperationStatus.Cancelled;
        bool? Finish(OutpostOperationReply reply)
        {
            if (Completed(reply)) return true;
            MarkReplyUnknown(entry.Id); return null;
        }
    }
}
