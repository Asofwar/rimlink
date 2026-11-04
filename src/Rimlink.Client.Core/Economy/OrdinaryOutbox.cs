using Google.Protobuf;
using Rimlink.Client.Net;
using Rimlink.Client.Outposts;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

/// <summary>Original intent and byte-exact removal proof; a refusal is not a return grant.</summary>
public sealed class OrdinaryJournal
{
    public byte[] Capture { get; set; } = [];
    public byte[]? RemovalProof { get; set; }
    public byte[]? AcceptedProof { get; set; }
    public byte[]? CanonicalProof { get; set; }
    public byte[]? ReturnRequest { get; set; }
    public byte[]? ReturnReply { get; set; }
    public string? ReturnBranchId { get; set; }
    public bool SpawnStarted { get; set; }
}

public sealed partial class TransferOutbox
{
    public OutboxEntry PrepareOrdinary(IMessage request, OutpostScopeMessage scope)
    {
        if (!HasOrdinaryPawns(request) || scope.SaveGeneration != generation || scope.SaveWriteEpoch != epoch)
            throw new InvalidDataException("ordinary journal scope differs from loaded save");
        return Create(request, OutboxPhase.Prepared, scope);
    }

    private static bool HasOrdinaryPawns(IMessage request) => request is TransferRequest or PostRequest or TradeOpenRequest
        or TradeSideRequest or LaunchRequest or RaidReport or LiveProposal
        && OrdinaryCapture.Identity(request).Pawns.Pawns.Any(p => p.Registry is { NonAuthoritativeMirror: false });

    private static bool IsOrdinary(OutboxEntry entry) => entry.Ordinary is not null || HasOrdinaryPawns(Decode(entry));

    private static OrdinaryOutgoingCapture ReadOrdinary(OutboxEntry entry)
    {
        var journal = entry.Ordinary ?? throw new InvalidDataException("ordinary journal capture missing");
        var capture = Parse(journal.Capture, OrdinaryOutgoingCapture.Parser);
        OrdinaryCapture.Validate(capture);
        if (entry.SaveGeneration != capture.Scope.SaveGeneration || entry.SaveWriteEpoch != capture.Scope.SaveWriteEpoch
            || !capture.Equals(OrdinaryCapture.Create(Decode(entry), entry.Id, capture.Scope)))
            throw new InvalidDataException("ordinary journal intent changed");
        return capture;
    }

    private static bool IsRemoval(SaveMeta snapshot, OrdinaryOutgoingCapture capture)
    {
        if (snapshot.Recovery is not { Outposts.Present: true } recovery || snapshot.RawSha256?.Length != 32) return false;
        recovery.Validate();
        if (!capture.Scope.Equals(recovery.Outposts.Metadata.Scope) || !recovery.Removed.Contains(capture.LocalOperationId)
            || recovery.Returned.Contains(capture.LocalOperationId)
            || !capture.Equals(recovery.Outposts.Metadata.OrdinaryOutgoing.SingleOrDefault(c => c.LocalOperationId == capture.LocalOperationId))) return false;
        foreach (var item in capture.Pawns)
        {
            var before = item.Pawn.Registry;
            var after = recovery.Outposts.Metadata.Registry.SingleOrDefault(r => r.PawnCustodyId == before.PawnCustodyId);
            if (after is null || !after.Tombstone || after.NonAuthoritativeMirror || !after.Scope.Equals(capture.Scope)
                || after.AssignmentId != before.AssignmentId || after.PawnReferenceToken != before.PawnReferenceToken) return false;
        }
        return true;
    }

    private static OutpostSnapshotProof RemovalProof(SaveMeta snapshot) => new() { Scope = snapshot.Recovery!.Outposts.Metadata.Scope.Clone(),
        SnapshotId = snapshot.Recovery.SnapshotId, RawSha256 = ByteString.CopyFrom(snapshot.RawSha256!) };

    /// <summary>Bind the frozen XML bytes before they can enter the upload queue. Never rebind to a later save.</summary>
    public void BindOrdinarySnapshot(string id, SaveMeta snapshot)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id); var capture = ReadOrdinary(entry);
            if (!IsRemoval(snapshot, capture)) throw new InvalidDataException("ordinary removal snapshot mismatch");
            byte[] proof = RemovalProof(snapshot).ToByteArray(); var journal = entry.Ordinary!;
            if (journal.RemovalProof is not null)
            {
                if (!journal.RemovalProof.AsSpan().SequenceEqual(proof)) throw new InvalidDataException("ordinary removal proof is immutable");
                return;
            }
            journal.RemovalProof = proof;
            try { Save(); } catch { journal.RemovalProof = null; throw; }
        }
    }

    private void ReconcileOrdinarySnapshot(SaveMeta snapshot, bool accepted, bool selected)
    {
        lock (gate)
        {
            foreach (var entry in entries.Where(e => IsOrdinary(e)).ToArray())
            {
                if (ReconcileOrdinaryReturn(entry, snapshot, accepted, selected) || entry.Phase == OutboxPhase.RecoveryRequired) continue;
                bool matching, original = false;
                try
                {
                    var capture = ReadOrdinary(entry); var journal = entry.Ordinary!;
                    var removal = journal.RemovalProof is null ? null : Parse(journal.RemovalProof, OutpostSnapshotProof.Parser);
                    original = removal is not null && IsRemoval(snapshot, capture) && removal.Equals(RemovalProof(snapshot));
                    bool originalAccepted = removal is not null && journal.AcceptedProof is not null
                        && removal.Equals(Parse(journal.AcceptedProof, OutpostSnapshotProof.Parser));
                    matching = removal is not null && IsRemoval(snapshot, capture) && (original || originalAccepted
                        && removal.SnapshotId != snapshot.Recovery!.SnapshotId);
                }
                catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException)
                { matching = false; }
                if (!matching) { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                if (!accepted) continue;
                if (original)
                {
                    entry.Ordinary!.AcceptedProof = RemovalProof(snapshot).ToByteArray();
                    entry.SnapshotId = snapshot.Recovery!.SnapshotId;
                }
                entry.Ordinary!.CanonicalProof = RemovalProof(snapshot).ToByteArray();
                entry.Phase = OutboxPhase.Ready;
            }
            Save();
        }
    }

    private static bool OrdinaryReadyFor(OutboxEntry entry, LoginReply login)
    {
        try
        {
            var capture = ReadOrdinary(entry); var journal = entry.Ordinary!;
            if (entry.Phase != OutboxPhase.Ready || journal.ReturnRequest is not null || entry.Refusal is not null || login.OutpostVersion != 1 || login.Player?.PlayerId != capture.Scope.OwnerPlayerId
                || login.OutpostRestoreBoundary != capture.Scope.RestoreBoundary || journal.RemovalProof is null || journal.AcceptedProof is null) return false;
            var proof = Parse(journal.RemovalProof, OutpostSnapshotProof.Parser);
            return OutpostReconciliation.ValidProof(proof) && proof.Scope.Equals(capture.Scope)
                && proof.Equals(Parse(journal.AcceptedProof, OutpostSnapshotProof.Parser));
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or ArgumentException) { return false; }
    }
}
