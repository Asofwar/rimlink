using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
namespace Rimlink.Client.Economy;

public sealed partial class TransferOutbox
{
    public OrdinaryCancelRequest PrepareOrdinaryReturn(string id, OutpostSnapshotProof canonical, string branchId)
    {
        lock (gate) {
            var entry = entries.Single(e => e.Id == id); var capture = ReadOrdinary(entry); var journal = entry.Ordinary!;
            if (!Guid.TryParseExact(branchId, "D", out var branch) || branch == Guid.Empty || branchId != branch.ToString("D")
                || !OutpostReconciliation.ValidProof(canonical) || !canonical.Scope.Equals(capture.Scope)
                || journal.RemovalProof is null || journal.AcceptedProof is null
                || !journal.RemovalProof.AsSpan().SequenceEqual(journal.AcceptedProof)) throw new InvalidDataException("ordinary return has no accepted removal proof");
            if (journal.ReturnRequest is not null) {
                var frozen = ReadOrdinaryReturn(entry);
                if (journal.ReturnBranchId != branchId || !frozen.CanonicalSnapshot.Equals(canonical)) throw new InvalidDataException("ordinary return intent and branch are immutable");
                return frozen;
            }
            if (entry.Phase != OutboxPhase.Ready || !canonical.Equals(Parse(journal.CanonicalProof ?? journal.AcceptedProof, OutpostSnapshotProof.Parser)))
                throw new InvalidDataException("ordinary return is not ready on its accepted canonical snapshot");
            var removal = Parse(journal.AcceptedProof, OutpostSnapshotProof.Parser);
            if (!OutpostReconciliation.ValidProof(removal) || !removal.Scope.Equals(capture.Scope)) throw new InvalidDataException("ordinary removal proof scope mismatch");
            var request = new OrdinaryCancelRequest { Scope = capture.Scope.Clone(), Capture = capture,
                OriginalRequest = ByteString.CopyFrom(entry.Payload), RemovalSnapshot = removal,
                CanonicalSnapshot = canonical.Clone(), ReturnAttemptId = Guid.NewGuid().ToString("D") };
            _ = OrdinaryReturnCodec.Payload(request);
            journal.ReturnRequest = request.ToByteArray(); journal.ReturnBranchId = branchId;
            try { Save(); } catch { journal.ReturnRequest = null; journal.ReturnBranchId = null; throw; }
            return request.Clone();
        }
    }
    private static OrdinaryCancelRequest ReadOrdinaryReturn(OutboxEntry entry)
    {
        var capture = ReadOrdinary(entry); var journal = entry.Ordinary!;
        var request = Parse(journal.ReturnRequest ?? throw new InvalidDataException("ordinary return intent missing"), OrdinaryCancelRequest.Parser);
        if (!request.Capture.Equals(capture) || !request.OriginalRequest.Span.SequenceEqual(entry.Payload)
            || journal.AcceptedProof is null || !request.RemovalSnapshot.Equals(Parse(journal.AcceptedProof, OutpostSnapshotProof.Parser)))
            throw new InvalidDataException("ordinary return journal changed");
        _ = OrdinaryReturnCodec.Payload(request);
        return request;
    }
    public void RecordOrdinaryReturnReply(string id, OrdinaryCancelRequest request, OrdinaryOperationReply reply)
    {
        lock (gate) {
            var entry = entries.Single(e => e.Id == id); var journal = entry.Ordinary!;
            if (!ReadOrdinaryReturn(entry).Equals(request)
                || reply.Status is not (OrdinaryOperationStatus.ReturnStarted or OrdinaryOperationStatus.ReturnAccepted)) throw new InvalidDataException("ordinary return reply mismatch");
            _ = OrdinaryReturnEvidence.Receipt(request, reply.ReturnProof);
            if (journal.ReturnReply is not null) {
                var previous = Parse(journal.ReturnReply, OrdinaryOperationReply.Parser);
                if (!previous.ReturnProof.Equals(reply.ReturnProof) || previous.Status == OrdinaryOperationStatus.ReturnAccepted && reply.Status != previous.Status)
                    throw new InvalidDataException("ordinary return authorization changed");
            }
            var old = (journal.ReturnReply, entry.Phase);
            journal.ReturnReply = reply.ToByteArray(); entry.Phase = OutboxPhase.Returning;
            try { Save(); } catch { (journal.ReturnReply, entry.Phase) = old; throw; }
        }
    }
    public void MarkOrdinaryReturnSpawnStarted(string id, string branchId)
    {
        lock (gate) {
            var entry = entries.Single(e => e.Id == id); var journal = entry.Ordinary!; var request = ReadOrdinaryReturn(entry);
            if (entry.Phase != OutboxPhase.Returning || journal.SpawnStarted || journal.ReturnBranchId != branchId || journal.ReturnReply is null
                || !Guid.TryParseExact(branchId, "D", out var branch) || branch == Guid.Empty) throw new InvalidDataException("ordinary return has no continuous pre-spawn authorization");
            var reply = Parse(journal.ReturnReply, OrdinaryOperationReply.Parser);
            if (reply.Status != OrdinaryOperationStatus.ReturnStarted) throw new InvalidDataException("ordinary return is acknowledgement only");
            _ = OrdinaryReturnEvidence.Receipt(request, reply.ReturnProof);
            journal.SpawnStarted = true;
            try { Save(); } catch { journal.SpawnStarted = false; throw; }
        }
    }
    private bool ReconcileOrdinaryReturn(OutboxEntry entry, SaveMeta snapshot, bool accepted, bool selected)
    {
        if (entry.Ordinary?.ReturnRequest is null) return false;
        bool matching = false;
        try {
            var request = ReadOrdinaryReturn(entry);
            if (entry.Ordinary.ReturnReply is not null && snapshot.RawSha256?.Length == 32
                && snapshot.Recovery is { Outposts.Present: true } recovery && recovery.Removed.Contains(entry.Id) && recovery.Returned.Contains(entry.Id)) {
                recovery.Validate(); var metadata = recovery.Outposts.Metadata;
                var reply = Parse(entry.Ordinary.ReturnReply, OrdinaryOperationReply.Parser);
                var receipt = OrdinaryReturnEvidence.Receipt(request, reply.ReturnProof);
                matching = metadata.Scope.Equals(request.Scope)
                    && request.Capture.Equals(metadata.OrdinaryOutgoing.SingleOrDefault(c => c.LocalOperationId == entry.Id))
                    && OrdinaryReturnEvidence.Decide(request, reply, metadata, "", "", true, true, true) == MaterializationDecision.AckOnly;
                foreach (var item in request.Capture.Pawns) {
                    var after = metadata.Registry.SingleOrDefault(r => r.PawnCustodyId == item.Pawn.Registry.PawnCustodyId);
                    matching &= after is { Tombstone: false, NonAuthoritativeMirror: false } && after.Scope.Equals(request.Scope)
                        && after.AssignmentId == item.Pawn.Registry.AssignmentId;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException) { matching = false; }
        if (matching) { if (accepted) entries.Remove(entry); else entry.Phase = OutboxPhase.Returning; }
        else if (selected) entry.Phase = OutboxPhase.RecoveryRequired;
        return true;
    }
}
