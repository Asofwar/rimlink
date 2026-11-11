using Google.Protobuf;
using Rimlink.Client.GuildCapture;
using Rimlink.Client.Net;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;

namespace Rimlink.Client.Economy;

public sealed class GuildDeliveryJournal
{
    public byte[] Parcel { get; set; } = [];
    public string BranchId { get; set; } = "";
    public byte[]? AcceptedProof { get; set; }
    public byte[]? AcceptedReceipt { get; set; }
    public byte[]? Authorization { get; set; }
    public bool SpawnStarted { get; set; }
    public bool ReplyUnknown { get; set; }
}

public sealed partial class TransferOutbox
{
    public OutboxEntry PrepareGuildDelivery(Parcel parcel, GuildDeliveryRequest request, string branchId)
    {
        GuildDeliveryEvidence.ValidateIntent(parcel, request);
        if (request.Scope.SaveGeneration != generation || request.Scope.SaveWriteEpoch != epoch
            || !Guid.TryParseExact(branchId, "D", out var branch) || branch == Guid.Empty) throw InvalidGuildJournal();
        var entry = new OutboxEntry { Id = Guid.NewGuid().ToString("N"), Kind = OutboxKind.GuildBeginDelivery,
            Payload = request.ToByteArray(), CreatedUtc = DateTime.UtcNow, Phase = OutboxPhase.Prepared,
            SaveGeneration = generation, SaveWriteEpoch = epoch,
            Guild = new() { Parcel = parcel.ToByteArray(), BranchId = branchId } };
        lock (gate)
        {
            foreach (var existing in entries.Where(e => e.Kind == OutboxKind.GuildBeginDelivery))
            {
                var prior = ReadGuild(existing);
                if (prior.Request.ParcelId != request.ParcelId && prior.Request.ClientRequestId != request.ClientRequestId) continue;
                if (!prior.Request.Equals(request) || !prior.Parcel.Equals(parcel) || existing.Guild!.BranchId != branchId) throw InvalidGuildJournal();
                return existing;
            }
            entries.Add(entry);
            try { Save(); } catch { entries.Remove(entry); throw; }
        }
        return entry;
    }

    private static (Parcel Parcel, GuildDeliveryRequest Request, GuildDeliveryAuthorization? Authorization) ReadGuild(OutboxEntry entry)
    {
        if (entry.Kind != OutboxKind.GuildBeginDelivery || entry.Payload.Length > 4096 || entry.Guild is not { } journal
            || journal.Parcel.Length > 8192 || !Guid.TryParseExact(journal.BranchId, "D", out var branch) || branch == Guid.Empty)
            throw InvalidGuildJournal();
        var parcel = Parse(journal.Parcel, Parcel.Parser); var request = Parse(entry.Payload, GuildDeliveryRequest.Parser);
        GuildDeliveryEvidence.ValidateIntent(parcel, request);
        if (entry.SaveGeneration != request.Scope.SaveGeneration || entry.SaveWriteEpoch != request.Scope.SaveWriteEpoch) throw InvalidGuildJournal();
        var authorization = journal.Authorization is null ? null : Parse(journal.Authorization, GuildDeliveryAuthorization.Parser);
        GuildDeliveryReceipt? expected = authorization is null ? null : GuildDeliveryEvidence.Receipt(parcel, request, authorization);
        if (journal.SpawnStarted && authorization is null || journal.AcceptedReceipt is not null && expected is null) throw InvalidGuildJournal();
        if (journal.AcceptedProof is not null)
        {
            var proof = Parse(journal.AcceptedProof, GuildSnapshotProof.Parser);
            if (!Guid.TryParseExact(proof.SnapshotId, "N", out var id) || id == Guid.Empty || proof.RawSha256.Length != 32
                || !proof.Scope.Equals(request.Scope) || journal.AcceptedReceipt is null && !proof.Equals(request.BeforeSnapshot)) throw InvalidGuildJournal();
        }
        if (journal.AcceptedReceipt is not null && (journal.AcceptedProof is null
            || !Parse(journal.AcceptedReceipt, GuildDeliveryReceipt.Parser).Equals(expected))) throw InvalidGuildJournal();
        return (parcel, request, authorization);
    }

    private static bool GuildReadyFor(OutboxEntry entry, LoginReply login)
    {
        try
        {
            var scope = ReadGuild(entry).Request.Scope;
            return login.GuildVersion == GuildResourceContracts.CurrentVersion && scope.PlayerId == login.Player.PlayerId
                && scope.RestoreBoundary == login.OutpostRestoreBoundary && entry.Phase == OutboxPhase.Ready && entry.Guild!.AcceptedProof is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidProtocolBufferException or ArgumentException) { return false; }
    }

    public void RecordGuildReply(string id, GuildDeliveryAuthorization reply)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id); var evidence = ReadGuild(entry); var journal = entry.Guild!;
            var receipt = GuildDeliveryEvidence.Receipt(evidence.Parcel, evidence.Request, reply);
            if (entry.Phase != OutboxPhase.Ready || journal.AcceptedProof is null
                || evidence.Authorization is { } prior && (!receipt.Equals(GuildDeliveryEvidence.Receipt(evidence.Parcel, evidence.Request, prior))
                    || prior.AckOnly && (!reply.AckOnly || !prior.AcceptedSnapshot.Equals(reply.AcceptedSnapshot)))) throw InvalidGuildJournal();
            var previous = (journal.Authorization, journal.ReplyUnknown);
            journal.Authorization = reply.ToByteArray(); journal.ReplyUnknown = false;
            try { Save(); } catch { (journal.Authorization, journal.ReplyUnknown) = previous; throw; }
        }
    }

    public void MarkGuildSpawnStarted(string id, string branchId)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id); var evidence = ReadGuild(entry); var journal = entry.Guild!;
            if (entry.Phase != OutboxPhase.Ready || journal.AcceptedProof is null || journal.BranchId != branchId
                || evidence.Authorization is not { AckOnly: false } || journal.SpawnStarted || journal.AcceptedReceipt is not null) throw InvalidGuildJournal();
            journal.SpawnStarted = true;
            try { Save(); } catch { journal.SpawnStarted = false; throw; }
        }
    }

    public void MarkGuildReplyUnknown(string id)
    {
        lock (gate)
        {
            var entry = entries.Single(e => e.Id == id); _ = ReadGuild(entry); var journal = entry.Guild!;
            bool prior = journal.ReplyUnknown; journal.ReplyUnknown = true;
            try { Save(); } catch { journal.ReplyUnknown = prior; throw; }
        }
    }

    private void ReconcileGuildSnapshot(SaveMeta snapshot, bool accepted, bool selected)
    {
        var recovery = snapshot.Recovery!;
        if (!accepted || snapshot.RawSha256?.Length != 32) return;
        lock (gate)
        {
            foreach (var entry in entries.Where(e => e.Kind == OutboxKind.GuildBeginDelivery && e.Phase != OutboxPhase.RecoveryRequired))
            {
                var evidence = ReadGuild(entry); var journal = entry.Guild!; var guild = recovery.Outposts.Metadata.GuildMetadata;
                if (guild is null || !guild.Scope.Equals(evidence.Request.Scope) || recovery.Generation != entry.SaveGeneration
                    || recovery.WriteEpoch != entry.SaveWriteEpoch)
                { if (selected) entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                var proof = new GuildSnapshotProof { Scope = guild.Scope.Clone(), SnapshotId = recovery.SnapshotId,
                    RawSha256 = ByteString.CopyFrom(snapshot.RawSha256) };
                var receipts = guild.Receipts.Where(r => r.ParcelId == evidence.Request.ParcelId
                    || r.ClientRequestId == evidence.Request.ClientRequestId || r.ClaimId == evidence.Parcel.GuildResourcePayload.ClaimId).ToArray();
                var expected = evidence.Authorization is null ? null : GuildDeliveryEvidence.Receipt(evidence.Parcel, evidence.Request, evidence.Authorization);
                if (receipts.Length != 0)
                {
                    if (receipts.Length != 1 || !receipts[0].Equals(expected)) { entry.Phase = OutboxPhase.RecoveryRequired; continue; }
                    journal.AcceptedReceipt = receipts[0].ToByteArray(); journal.AcceptedProof = proof.ToByteArray();
                    entry.SnapshotId = proof.SnapshotId; entry.Phase = OutboxPhase.Ready;
                }
                else if (journal.AcceptedReceipt is not null || selected && journal.SpawnStarted)
                    entry.Phase = OutboxPhase.RecoveryRequired;
                else if (!journal.SpawnStarted && proof.Equals(evidence.Request.BeforeSnapshot))
                { journal.AcceptedProof = proof.ToByteArray(); entry.SnapshotId = proof.SnapshotId; entry.Phase = OutboxPhase.Ready; }
                else if (selected) entry.Phase = OutboxPhase.RecoveryRequired;
            }
            Save();
        }
    }

    private async Task<bool?> AttemptGuild(GuildCaptureClient client, OutboxEntry entry, CancellationToken ct)
    {
        var evidence = ReadGuild(entry);
        var reply = await client.BeginDelivery(evidence.Request, ct); RecordGuildReply(entry.Id, reply);
        if (reply.AckOnly && entry.Guild!.AcceptedReceipt is null)
        { HoldForRecovery(entry.Id, new(null, "accepted guild delivery is missing from the selected home snapshot", null, [])); return null; }
        return reply.AckOnly ? true : null;
    }

    private static bool CanRemoveGuild(OutboxEntry entry)
        => ReadGuild(entry).Authorization is { AckOnly: true } && entry.Guild!.AcceptedReceipt is not null && entry.Phase == OutboxPhase.Ready;
    private static InvalidDataException InvalidGuildJournal() => new("guild-delivery-journal-invalid");
}
